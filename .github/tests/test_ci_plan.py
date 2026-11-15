"""Push build selection, promotion guard and notification formatting."""
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/scripts'))
import ci_notify  # noqa: E402
from ci_plan import SERVICES, plan  # noqa: E402
import promote_backend  # noqa: E402


def built(paths):
    return sorted(s['name'] for s in plan('dev', paths)['backend'])


class CiPlanTests(unittest.TestCase):
    def test_catalog_matches_repository(self):
        for service in SERVICES:
            with self.subTest(service=service['name']):
                self.assertTrue((ROOT / service['project']).is_file())
                self.assertTrue((ROOT / service['dockerfile']).is_file())
                if service['node_dir']:
                    self.assertTrue((ROOT / service['node_dir'] / 'package-lock.json').is_file())
        self.assertEqual(len({s['image'] for s in SERVICES}), len(SERVICES))
        # Every backend service with a Dockerfile is published.
        dockerized = sorted(p.parent.name.split('.', 1)[1].lower() for p in (ROOT / 'Backend').glob('*/Dockerfile'))
        self.assertEqual(sorted(s['name'] for s in SERVICES), dockerized)

    def test_service_change_builds_only_that_service(self):
        self.assertEqual(built(['Backend/BarkCloud.Files/Program.cs']), ['files'])
        self.assertEqual(built(['Backend/BarkCloud.Transcoder/Program.cs']), ['transcoder'])
        self.assertEqual(built(['Backend/BarkCloud.Web/ClientApp/src/App.tsx']), ['web'])
        self.assertEqual(built(['FolderIcons/folder.svg']), ['web'])

    def test_shared_inputs_rebuild_only_images_that_build_from_them(self):
        everything = sorted(s['name'] for s in SERVICES)
        grpc_services = [name for name in everything if name != 'transcoder']
        self.assertEqual(built(['Backend/BarkCloud.GrpcServer/Interceptor.cs']), grpc_services)
        self.assertEqual(built(['Directory.Build.props']), everything)
        self.assertEqual(built(['Backend/rebuild.trigger']), everything)
        self.assertEqual(built(['.github/actions/docker-version/compute-version.sh']), everything)
        self.assertNotIn('transcoder', built(['Shared/BarkCloud.Shared.Queue/Event.cs']))
        self.assertEqual(built(None), everything)

    def test_tests_docs_and_ci_do_not_publish_images(self):
        for path in ['Tests/Backend/BarkCloud.Files.Tests/Test.cs', 'Tests/BarkCloud.TestKit/Helpers.cs',
                     '.github/tests/test_ci_plan.py', '.github/workflows/test-backend.yml',
                     '.github/workflows/ci.yml', '.github/scripts/ci_notify.py',
                     'Obsidian/BarkCloudVault/Index.md', 'AGENTS.md', 'Android/BarkCloud.Android/app/A.kt',
                     'Drive/BarkCloud.Drive.App/App.cs', 'Tools/BarkCloud.Builder/BuilderModel.cs']:
            with self.subTest(path=path):
                self.assertEqual(built([path]), [])

    def test_master_copies_images_instead_of_building(self):
        result = plan('master', ['Backend/BarkCloud.Files/Program.cs'])
        self.assertEqual(result['backend'], [])
        self.assertTrue(result['promote'])
        self.assertFalse(plan('dev', None)['promote'])


class PromotionGuardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.git('init', '-b', 'dev')
        self.commit('Backend/BarkCloud.Files/A.cs', 'v1')
        self.built = self.head()
        self.old_root = promote_backend.ROOT
        promote_backend.ROOT = self.repo
        self.addCleanup(setattr, promote_backend, 'ROOT', self.old_root)
        self.sources = next(s for s in SERVICES if s['name'] == 'files')['sources']

    def git(self, *args):
        return subprocess.check_output(['git', '-c', 'user.name=T', '-c', 'user.email=t@example.invalid',
                                        '-c', 'commit.gpgsign=false', *args], cwd=self.repo, text=True)

    def commit(self, path, text):
        target = self.repo / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)
        self.git('add', path)
        self.git('commit', '-m', text)

    def head(self):
        return self.git('rev-parse', 'HEAD').strip()

    def test_image_is_valid_while_its_sources_are_unchanged(self):
        self.commit('Backend/BarkCloud.Users/B.cs', 'other service')
        self.commit('Tests/Backend/BarkCloud.Files.Tests/T.cs', 'tests only')
        self.commit('Backend/BarkCloud.Transcoder/F.cs', 'unrelated service')
        promote_backend.check_revision(self.built, self.sources)

    def test_newer_sources_reject_stale_image(self):
        for path in ['Backend/BarkCloud.Files/A.cs', 'Shared/BarkCloud.Proto/files_api.proto',
                     'Backend/BarkCloud.GrpcServer/G.cs', 'Directory.Build.props']:
            with self.subTest(path=path):
                self.commit(path, 'changed ' + path)
                with self.assertRaisesRegex(promote_backend.PromotionError, 'исходники изменились'):
                    promote_backend.check_revision(self.built, self.sources)

    def test_image_from_commit_outside_branch_or_unlabelled_is_rejected(self):
        self.git('checkout', '-q', '-b', 'side')
        self.commit('Backend/BarkCloud.Files/A.cs', 'side')
        side = self.head()
        self.git('checkout', '-q', 'dev')
        with self.assertRaisesRegex(promote_backend.PromotionError, 'нет в этой ветке'):
            promote_backend.check_revision(side, self.sources)
        with self.assertRaisesRegex(promote_backend.PromotionError, 'нет метки'):
            promote_backend.check_revision('', self.sources)
        with self.assertRaisesRegex(promote_backend.PromotionError, 'не найден'):
            promote_backend.check_revision('0' * 40, self.sources)


class NotifyTests(unittest.TestCase):
    ENV = {'GITHUB_REF_NAME': 'dev', 'GITHUB_SHA': 'a' * 40, 'GITHUB_ACTOR': 'some_user'}

    def test_push_summary_lists_components_and_failures(self):
        results = [('success', '🐳 files', '1.0.5'), ('failure', '📱 Android', '')]
        message = ci_notify.push_message(self.ENV, results, 'success')
        self.assertIn('❌ *Сборка с ошибками*', message)
        self.assertIn('✅ 🐳 files `1.0.5`', message)
        self.assertIn('❌ 📱 Android', message)
        self.assertIn('`some_user`', message)

    def test_jobs_that_died_without_result_file_are_reported(self):
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, 'backend-files.txt').write_text('failure|🐳 files|\n', encoding='utf-8')
            Path(directory, 'android.txt').write_text('success|📱 Android|v1.2.3\n', encoding='utf-8')
            results, failed_jobs = ci_notify.read_results(directory)
        needs = {'backend': {'result': 'failure'}, 'promote': {'result': 'skipped'},
                 'android': {'result': 'success'}, 'drive': {'result': 'cancelled'}}
        extra = ci_notify.missing_failures(needs, failed_jobs)
        self.assertEqual(failed_jobs, {'backend'})
        self.assertEqual(extra, [('cancelled', '🪟 Drive', '')])

    def test_pr_message_escapes_untrusted_title_and_branches(self):
        env = dict(self.ENV, REQUIRED='failure', PR_NUMBER='7', PR_TITLE='fix *bold* _x_ `code` [l](u)',
                   HEAD_REF='feat/`x`', BASE_REF='dev', GUARD='success', BACKEND='failure',
                   FILES_INTEGRATION='success')
        message = ci_notify.pr_message(env)
        title_line = next(line for line in message.splitlines() if line.startswith('📝'))
        self.assertIsNone(re.search(r'[*_`\[\]]', title_line))
        self.assertIn("`feat/'x'` → `dev`", message)
        self.assertIn('❌ Backend тесты', message)
        self.assertIn('✅ Files: durable upload', message)
        self.assertIn('✅ Ветка-источник', message)


if __name__ == '__main__':
    unittest.main()
