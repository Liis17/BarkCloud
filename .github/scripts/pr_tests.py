"""Select affected PR test suites, build the backend test matrix and validate the aggregate result."""
from functools import lru_cache
import json
import os
from pathlib import Path
import subprocess
import sys

from ci_plan import project_inputs

SUITES = ('backend', 'files_integration', 'windows', 'android', 'apple', 'frontend', 'scripts')
JOBS = ('backend', 'files_integration', 'clients', 'frontend', 'scripts')
CLIENT_PLATFORMS = ('windows', 'android', 'apple')
ROOT = Path(__file__).resolve().parents[2]
SELECTOR_FILES = ('.github/workflows/ci.yml', '.github/scripts/pr_tests.py', '.github/scripts/ci_plan.py')
# Change how all .NET code is built or tested: every backend test project runs.
BACKEND_EVERYTHING = ('Directory.Build.props', 'Tests/Directory.Build.props', '.github/workflows/test-backend.yml',
                      '.github/actions/test-containers/action.yml', '.github/scripts/start-test-containers.sh')
# Always added by test-backend.yml when a project list is given.
COMMON_BACKEND_TESTS = 'BarkCloud.GrpcServer.Tests'
# Durable upload scenarios run their own docker compose stack in files-upload-integration.yml.
FILES_INTEGRATION = 'BarkCloud.Files.IntegrationTests'
# Containers a test project needs from .github/actions/test-containers.
CONTAINERS = {
    'BarkCloud.Files.Tests': ('postgres',),
    'BarkCloud.Users.Tests': ('postgres',),
    'BarkCloud.Identity.IntegrationTests': ('postgres',),
    'BarkCloud.Users.IntegrationTests': ('postgres', 'rabbitmq'),
}
# Built only on Windows; tests link the few files they need, so other changes here need no backend run.
WINDOWS_ONLY = ('Drive/', 'Tools/BarkCloud.Builder/')
# net10.0 projects whose tests assume Windows paths (Builder stores D:\ paths); they run in the Windows suite.
WINDOWS_TESTS = {'BarkCloud.Builder.Tests'}
# Code outside any known test input: run the whole backend suite rather than miss a test.
BACKEND_FALLBACK = ('Backend/', 'Shared/', 'Tests/', 'Tools/')


@lru_cache(maxsize=None)
def test_projects(root=ROOT):
    """{test project name: (relative path, is Windows-only, repository paths it depends on)}."""
    projects = {}
    for csproj in sorted((root / 'Tests').rglob('*.csproj')):
        relative = csproj.relative_to(root).as_posix()
        if csproj.stem == 'BarkCloud.TestKit' or '/bin/' in relative or '/obj/' in relative:
            continue
        inputs = set()
        project_inputs(root, relative, inputs, set())
        windows = csproj.stem in WINDOWS_TESTS or '-windows' in csproj.read_text(encoding='utf-8')
        projects[csproj.stem] = (relative, windows, inputs)
    return projects


@lru_cache(maxsize=None)
def untested_services(root):
    """Backend project folders that no test project builds from."""
    tested = set().union(*(inputs for _, _, inputs in test_projects(root).values()))
    return tuple(f'{csproj.parent.relative_to(root).as_posix()}/'
                 for csproj in sorted((root / 'Backend').glob('*/*.csproj'))
                 if f'{csproj.parent.relative_to(root).as_posix()}/' not in tested)


def depends(path, inputs):
    return any(path == item or (item.endswith('/') and path.startswith(item)) for item in inputs)


def select(paths, projects=None):
    """Return (suites, backend test projects); None for projects means every backend test project."""
    projects = test_projects() if projects is None else projects
    selected = dict.fromkeys(SUITES, False)
    backend_tests, backend_everything = set(), False
    for path in paths:
        if not path:
            continue
        if path in SELECTOR_FILES:
            return dict.fromkeys(SUITES, True), None
        users = {name for name, (_, _, inputs) in projects.items() if depends(path, inputs)}
        windows_users = {name for name in users if projects[name][1]}
        backend_users = users - windows_users
        if windows_users or path.startswith(WINDOWS_ONLY):
            selected['windows'] = True
        if path in BACKEND_EVERYTHING or path.endswith(('.props', '.targets')):
            selected['backend'] = selected['windows'] = backend_everything = True
        elif backend_users:
            selected['backend'] = True
            backend_tests |= backend_users
        elif path.startswith(untested_services(ROOT)):
            # Services without their own test project are covered only by the common GrpcServer tests.
            selected['backend'] = True
        elif (path.startswith(BACKEND_FALLBACK) and not path.startswith(WINDOWS_ONLY)
              and not windows_users and not path.endswith('.md')):
            selected['backend'] = backend_everything = True
        if path.startswith('Android/'):
            selected['android'] = True
        if path.startswith('Mac/BarkCloudKit/'):
            selected['apple'] = True
        if path == '.github/workflows/test-clients.yml':
            selected.update(dict.fromkeys(CLIENT_PLATFORMS, True))
        if path.startswith('Backend/BarkCloud.Web/ClientApp/'):
            selected['frontend'] = True
        if path == '.github/workflows/files-upload-integration.yml':
            selected['files_integration'] = True
        if path.startswith('.github/'):
            selected['scripts'] = True
    if backend_everything:
        selected['files_integration'] = True
        return selected, None
    if FILES_INTEGRATION in backend_tests:
        selected['files_integration'] = True
        backend_tests.discard(FILES_INTEGRATION)
    if not selected['backend']:
        return selected, None
    return selected, sorted(backend_tests | {COMMON_BACKEND_TESTS})


def select_suites(paths, projects=None):
    return select(paths, projects)[0]


def selected_jobs(suites):
    return {
        'backend': suites['backend'],
        'files_integration': suites['files_integration'],
        'clients': any(suites[platform] for platform in CLIENT_PLATFORMS),
        'frontend': suites['frontend'],
        'scripts': suites['scripts'],
    }


def backend_matrix(names, projects=None):
    """Matrix entries for test-backend.yml; an empty list of names means every backend test project."""
    projects = test_projects() if projects is None else projects
    available = {name for name, (_, windows, _) in projects.items() if not windows and name != FILES_INTEGRATION}
    if names:
        unknown = set(names) - available
        if unknown:
            raise SystemExit(f'Unknown backend test project(s): {", ".join(sorted(unknown))}')
        chosen = set(names) | {COMMON_BACKEND_TESTS}
    else:
        chosen = available
    return [{'name': name, 'project': projects[name][0],
             'postgres': 'postgres' in CONTAINERS.get(name, ()),
             'rabbitmq': 'rabbitmq' in CONTAINERS.get(name, ())}
            for name in sorted(chosen)]


def results_pass(selection_result, selected, results, guard_result='success'):
    """Every selected job must succeed and every other one must be skipped.

    Promotion PRs (into master) skip selection entirely: their code was tested on the way
    into dev, so only the source-branch guard has to pass.
    """
    if guard_result != 'success':
        return False
    if selection_result == 'skipped':
        return all(results[job] == 'skipped' for job in JOBS)
    return selection_result == 'success' and all(
        results[job] == ('success' if selected.get(job) else 'skipped') for job in JOBS)


def write_outputs(outputs):
    with Path(os.environ['GITHUB_OUTPUT']).open('a', encoding='utf-8') as output:
        for name, value in outputs.items():
            output.write(f'{name}={value}\n')


def main():
    if sys.argv[1:] == ['--check']:
        selected = json.loads(os.environ.get('SELECTED_JOBS') or '{}')
        results = {job: os.environ[job.upper()] for job in JOBS}
        print(f'Guard: {os.environ["GUARD"]}; selected jobs: {selected}; results: {results}')
        return 0 if results_pass(os.environ['SELECTION_RESULT'], selected, results, os.environ['GUARD']) else 1

    if sys.argv[1:2] == ['--matrix']:
        names = [name.strip() for name in (sys.argv[2] if len(sys.argv) > 2 else '').split(',') if name.strip()]
        matrix = backend_matrix(names)
        write_outputs({'matrix': json.dumps(matrix, separators=(',', ':'))})
        print('\n'.join(entry['project'] for entry in matrix))
        return 0

    if os.environ['GITHUB_EVENT_NAME'] == 'workflow_dispatch':
        suites, backend_projects = dict.fromkeys(SUITES, True), None
    else:
        # checkout fetch-depth: 2 includes the synthetic PR merge and its base parent.
        # Comparing their trees excludes changes already present in the target branch.
        paths = subprocess.check_output([
            'git', 'diff', '--no-renames', '--name-only', '-z', 'HEAD^1', 'HEAD'
        ]).decode('utf-8').split('\0')
        suites, backend_projects = select(paths)

    outputs = {name: str(enabled).lower() for name, enabled in suites.items()}
    outputs['platforms'] = json.dumps([name for name in CLIENT_PLATFORMS if suites[name]])
    outputs['selected'] = json.dumps(selected_jobs(suites))
    # Empty means every backend test project (the test-backend.yml default).
    outputs['backend_projects'] = ','.join(backend_projects or [])
    write_outputs(outputs)
    print(json.dumps(suites))
    print('backend test projects:', ', '.join(backend_projects) if backend_projects else 'all')
    return 0


if __name__ == '__main__':
    sys.exit(main())
