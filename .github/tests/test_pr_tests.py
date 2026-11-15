"""Regression tests for PR scope selection, the backend matrix and the required test gate."""
import itertools
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/scripts'))
from pr_tests import (COMMON_BACKEND_TESTS, JOBS, SUITES, backend_matrix, results_pass,  # noqa: E402
                      select, select_suites)


class PrTestSelectionTests(unittest.TestCase):
    def assert_suites(self, paths, expected):
        selected = select_suites(paths)
        self.assertEqual({name for name, enabled in selected.items() if enabled}, set(expected))

    def test_platform_sources_select_their_own_suite(self):
        for path, suites in [
            ('Android/BarkCloud.Android/app/src/main/res/drawable/ic.xml', ['android']),
            ('Drive/BarkCloud.Drive.App/App.xaml.cs', ['windows']),
            ('Tests/Drive/BarkCloud.Drive.Engine.Tests/Example.cs', ['windows']),
            ('Tools/BarkCloud.Builder/MainWindow.xaml', ['windows']),
            ('Tools/BarkCloud.Builder/BuilderModel.cs', ['windows']),
            # Builder embeds provisioning scripts into its output.
            ('Backend/provision-isolation.sh', ['windows']),
            ('Mac/BarkCloudKit/Sources/Example.swift', ['apple']),
        ]:
            with self.subTest(path=path):
                self.assert_suites([path], suites)

    def test_web_client_selects_frontend_and_web_tests(self):
        self.assert_suites(['Backend/BarkCloud.Web/ClientApp/src/App.tsx'], ['backend', 'frontend'])
        self.assertEqual(select(['Backend/BarkCloud.Web/ClientApp/src/App.tsx'])[1],
                         ['BarkCloud.GrpcServer.Tests', 'BarkCloud.Web.Tests'])

    def test_selector_changes_run_every_suite(self):
        for path in ['.github/workflows/ci.yml', '.github/scripts/ci_plan.py', '.github/scripts/pr_tests.py']:
            with self.subTest(path=path):
                self.assertEqual(select([path]), (dict.fromkeys(SUITES, True), None))

    def test_backend_runs_only_test_projects_that_build_from_the_change(self):
        for paths, expected in [
            (['Backend/BarkCloud.Torrent/Program.cs'], {'BarkCloud.Torrent.Tests'}),
            (['Backend/BarkCloud.Users/Program.cs', 'Tests/Backend/BarkCloud.Users.Tests/T.cs'],
             {'BarkCloud.Users.Tests', 'BarkCloud.Users.IntegrationTests'}),
        ]:
            with self.subTest(paths=paths):
                self.assertEqual(set(select(paths)[1]), expected | {COMMON_BACKEND_TESTS})
        self.assertGreater(len(select(['Shared/BarkCloud.Shared.Queue/Event.cs'])[1]), 5)

    def test_files_changes_run_durable_upload_scenarios_outside_the_matrix(self):
        suites, projects = select(['Backend/BarkCloud.Files/Upload/Service.cs'])
        self.assertTrue(suites['files_integration'])
        self.assertNotIn('BarkCloud.Files.IntegrationTests', projects)
        self.assertIn('BarkCloud.Files.Tests', projects)
        self.assert_suites(['.github/workflows/files-upload-integration.yml'], ['files_integration', 'scripts'])
        self.assertFalse(select_suites(['Backend/BarkCloud.Identity/Program.cs'])['files_integration'])

    def test_shared_proto_reaches_drive(self):
        suites = select_suites(['Shared/BarkCloud.Proto/files_api.proto'])
        self.assertTrue(suites['backend'])
        self.assertTrue(suites['windows'])

    def test_unmapped_backend_inputs_run_every_backend_test(self):
        for path in ['Backend/docker-compose.yml', 'Backend/rebuild.trigger', 'Directory.Build.props',
                     'Tests/Directory.Build.props', 'Tests/Backend/migrate-secrets-test.sh']:
            with self.subTest(path=path):
                suites, projects = select([path])
                self.assertTrue(suites['backend'])
                self.assertTrue(suites['files_integration'])
                self.assertIsNone(projects)

    def test_docs_do_not_start_application_tests(self):
        self.assert_suites(['AGENTS.md', 'Obsidian/BarkCloudVault/Platform/Testing.md',
                            'Mac/README.md'], [])

    def test_ci_helper_change_checks_scripts(self):
        self.assert_suites(['.github/scripts/send-telegram.sh'], ['scripts'])

    def test_client_test_workflow_runs_all_clients_and_scripts(self):
        self.assert_suites(['.github/workflows/test-clients.yml'], ['windows', 'android', 'apple', 'scripts'])

    def test_backend_matrix_marks_container_needs(self):
        matrix = {entry['name']: entry for entry in backend_matrix([])}
        self.assertNotIn('BarkCloud.Files.IntegrationTests', matrix)
        self.assertNotIn('BarkCloud.Drive.Engine.Tests', matrix)
        self.assertNotIn('BarkCloud.TestKit', matrix)
        self.assertNotIn('BarkCloud.Builder.Tests', matrix)
        self.assertEqual((matrix['BarkCloud.Users.IntegrationTests']['postgres'],
                          matrix['BarkCloud.Users.IntegrationTests']['rabbitmq']), (True, True))
        self.assertEqual((matrix['BarkCloud.Files.Tests']['postgres'],
                          matrix['BarkCloud.Files.Tests']['rabbitmq']), (True, False))
        self.assertFalse(matrix['BarkCloud.Web.Tests']['postgres'])
        for entry in matrix.values():
            self.assertTrue((ROOT / entry['project']).is_file(), entry)
        chosen = [entry['name'] for entry in backend_matrix(['BarkCloud.Torrent.Tests'])]
        self.assertEqual(chosen, [COMMON_BACKEND_TESTS, 'BarkCloud.Torrent.Tests'])
        with self.assertRaises(SystemExit):
            backend_matrix(['BarkCloud.Missing.Tests'])

    def test_gate_rejects_failed_cancelled_and_unexpectedly_skipped_jobs(self):
        states = ('success', 'failure', 'cancelled', 'skipped')
        for selected_values in itertools.product((False, True), repeat=len(JOBS)):
            selected = dict(zip(JOBS, selected_values))
            expected = {job: 'success' if selected[job] else 'skipped' for job in JOBS}
            for values in itertools.product(states, repeat=len(JOBS)):
                results = dict(zip(JOBS, values))
                for selection_result in ('success', 'failure', 'cancelled'):
                    self.assertEqual(results_pass(selection_result, selected, results),
                                     selection_result == 'success' and results == expected,
                                     (selection_result, selected, results))

    def test_promotion_pr_needs_only_guard_and_no_tests(self):
        skipped = dict.fromkeys(JOBS, 'skipped')
        self.assertTrue(results_pass('skipped', {}, skipped, 'success'))
        for guard in ('failure', 'cancelled', 'skipped'):
            self.assertFalse(results_pass('skipped', {}, skipped, guard))
            self.assertFalse(results_pass('success', dict.fromkeys(JOBS, False), skipped, guard))
        for job in JOBS:
            self.assertFalse(results_pass('skipped', {}, dict(skipped, **{job: 'success'}), 'success'))

    def test_shallow_merge_diff_excludes_unrelated_target_branch_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            repo = directory / 'source'
            repo.mkdir()

            def git(*args):
                return subprocess.check_output([
                    'git', '-c', 'user.name=Tests', '-c', 'user.email=tests@example.invalid',
                    '-c', 'commit.gpgsign=false', *args
                ], cwd=repo, stderr=subprocess.STDOUT)

            def commit_file(path, message):
                target = repo / path
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(message)
                git('add', path)
                git('commit', '-m', message)

            git('init', '-b', 'target')
            commit_file('README.md', 'base')
            git('checkout', '-b', 'android-change')
            commit_file('Android/BarkCloud.Android/app/Example.kt', 'android')
            git('checkout', 'target')
            commit_file('Backend/BarkCloud.Files/Example.cs', 'unrelated target change')
            git('merge', '--no-ff', '-m', 'synthetic PR merge', 'android-change')
            checkout = directory / 'checkout'
            git('clone', '--depth', '2', repo.as_uri(), str(checkout))
            output = directory / 'output'
            subprocess.run([sys.executable, str(ROOT / '.github/scripts/pr_tests.py')],
                           cwd=checkout, check=True, capture_output=True,
                           env=dict(os.environ, GITHUB_EVENT_NAME='pull_request', GITHUB_OUTPUT=str(output)))
            values = dict(line.split('=', 1) for line in output.read_text().splitlines())
            self.assertEqual(json.loads(values['platforms']), ['android'])
            self.assertEqual(json.loads(values['selected']), {'backend': False, 'files_integration': False,
                                                              'clients': True, 'frontend': False,
                                                              'scripts': False})


if __name__ == '__main__':
    unittest.main()
