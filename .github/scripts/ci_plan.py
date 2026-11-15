"""Select which backend images a push to dev builds and publish the backend service catalog."""
from fnmatch import fnmatchcase
import json
import os
from pathlib import Path
import posixpath
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
# Image inputs outside the .csproj graph: shared MSBuild properties and the Docker context filter.
BACKEND_COMMON = ('Directory.Build.props', '.dockerignore')
# Files that change how images are built or published, not what they contain.
BACKEND_TOOLING = ('.github/actions/docker-version/**', '.github/scripts/push-docker-image.sh',
                   'Backend/rebuild.trigger')
# MSBuild items whose Include is not a file path.
NON_FILE_ITEMS = ('PackageReference', 'FrameworkReference', 'Using', 'InternalsVisibleTo', 'PackageVersion')


def project_inputs(root, csproj, inputs, seen):
    """Collect repository paths a project builds from: its folder, referenced projects and linked files."""
    if csproj in seen or not (root / csproj).is_file():
        return
    seen.add(csproj)
    directory = posixpath.dirname(csproj)
    inputs.add(directory + '/')
    for item in ET.parse(root / csproj).iter():
        tag = item.tag.rsplit('}', 1)[-1]
        include = item.attrib.get('Include', '')
        if tag in NON_FILE_ITEMS or not include or '$(' in include:
            continue
        target = posixpath.normpath(posixpath.join(directory, include.replace(chr(92), '/')))
        if target.startswith('..'):
            continue
        if tag == 'ProjectReference':
            project_inputs(root, target, inputs, seen)
        elif '*' in target:
            inputs.add(target.split('*', 1)[0])
        else:
            inputs.add(target)


def project_patterns(csproj, root=ROOT):
    """Patterns for everything a project builds from, so a change elsewhere in Shared/ does not rebuild it."""
    inputs = set()
    project_inputs(root, csproj, inputs, set())
    patterns = set()
    for path in inputs:
        if path.endswith('/'):
            patterns.add(path + '**')
        elif (root / path).is_file():
            patterns.add(path)
        else:  # wildcard prefix such as Protos/api_ from Protos/api_*.proto
            patterns.add(posixpath.dirname(path) + '/**')
    return sorted(patterns)


def service(name, dockerfile='Dockerfile.slim', extra=(), node=None):
    directory = f'Backend/BarkCloud.{name.capitalize()}'
    project_path = f'{directory}/BarkCloud.{name.capitalize()}.csproj'
    return {
        'name': name,
        'image': f'barkcloud-{name}',
        'project': project_path,
        'dockerfile': f'./{directory}/{dockerfile}',
        'node_dir': node[0] if node else '',
        'node_version': node[1] if node else '',
        # Source inputs: a published image is valid for a commit only if these are unchanged.
        'sources': sorted({f'{directory}/**', *extra, *BACKEND_COMMON, *project_patterns(project_path)}),
    }


SERVICES = [
    service('configuration'),
    service('files'),
    service('identity'),
    service('notification'),
    service('torrent'),
    # The sidecar builds inside its own Dockerfile and ships ffmpeg.
    service('transcoder', dockerfile='Dockerfile'),
    service('users'),
    service('web', extra=('FolderIcons/**',), node=('Backend/BarkCloud.Web/ClientApp', '22')),
]


def matches(path, pattern):
    if pattern.startswith('**/'):
        return fnmatchcase(path.rsplit('/', 1)[-1], pattern[3:])
    if pattern.endswith('/**'):
        return path.startswith(pattern[:-2])
    return path == pattern


def touches(paths, patterns):
    return any(matches(path, pattern) for path in paths for pattern in patterns)


def plan(branch, paths):
    """paths=None means everything (manual run, new branch or unknown base).

    Editing ci.yml itself publishes no images: it also holds PR checks and notifications.
    After changing build steps, rebuild explicitly with workflow_dispatch mode=build.
    Client releases are built on every push, so the plan covers only backend images.
    """
    if branch == 'dev':
        return {'backend': [s for s in SERVICES
                            if paths is None or touches(paths, (*s['sources'], *BACKEND_TOOLING))],
                'promote': False}
    # master never rebuilds images: it copies the tested dev image when it differs.
    return {'backend': [], 'promote': True}


def changed_paths(before, sha):
    if not before or set(before) == {'0'}:
        return None
    if subprocess.run(['git', 'cat-file', '-e', f'{before}^{{commit}}'], capture_output=True).returncode:
        return None
    output = subprocess.check_output(['git', 'diff', '--no-renames', '--name-only', '-z', before, sha])
    return [path for path in output.decode('utf-8').split('\0') if path]


def main():
    branch = os.environ['GITHUB_REF_NAME']
    if branch not in ('dev', 'master'):
        sys.exit(f'Ветка {branch} не привязана к каналу сборок')
    paths = None
    if os.environ['GITHUB_EVENT_NAME'] == 'push':
        paths = changed_paths(os.environ.get('BEFORE', ''), os.environ['GITHUB_SHA'])
    result = plan(branch, paths)
    outputs = {
        'backend': json.dumps(result['backend'], separators=(',', ':')),
        'has_backend': str(bool(result['backend'])).lower(),
        'promote': str(result['promote']).lower(),
    }
    with Path(os.environ['GITHUB_OUTPUT']).open('a', encoding='utf-8') as output:
        for name, value in outputs.items():
            output.write(f'{name}={value}\n')
    print('promote:', result['promote'])
    print('backend:', ', '.join(s['name'] for s in result['backend']) or '-')
    return 0


if __name__ == '__main__':
    sys.exit(main())
