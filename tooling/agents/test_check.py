"""Integrity regressions using disposable copies, not application acceptance."""
import importlib.util
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location('agent_check', Path(__file__).with_name('check.py'))
check = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(check)


class SkillPackageChecks(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        shutil.copytree(check.ROOT / '.agents', self.root / '.agents')
        shutil.copytree(check.ROOT / 'docs', self.root / 'docs')
        shutil.copyfile(check.ROOT / 'AGENTS.md', self.root / 'AGENTS.md')
        self.skill = self.root / '.agents/skills/chunkmaestro-dotnet'

    def edit(self, path, before, after):
        text = path.read_text(encoding='utf-8')
        self.assertIn(before, text)
        path.write_text(text.replace(before, after), encoding='utf-8')

    def rejected(self, fragment):
        errors = check.validate(self.root)
        self.assertTrue(any(fragment in error for error in errors), errors)

    def test_all_packages_and_routing_are_valid(self):
        self.assertEqual(check.validate(self.root), [])

    def test_mismatched_package_name_is_rejected(self):
        self.edit(self.skill / 'SKILL.md', 'name: "chunkmaestro-dotnet"', 'name: "wrong-name"')
        self.rejected('mismatched skill name')

    def test_missing_metadata_is_rejected(self):
        (self.skill / 'agents/openai.yaml').unlink()
        self.rejected('openai.yaml')

    def test_missing_local_reference_is_rejected(self):
        self.edit(self.skill / 'SKILL.md', '../../../AGENTS.md', '../../../absent.md')
        self.rejected('missing link')

    def test_encoded_outside_link_is_rejected(self):
        self.edit(self.skill / 'SKILL.md', '../../../AGENTS.md', '../../../%2e%2e/outside.md')
        self.rejected('outside repository')

    def test_absolute_machine_link_is_rejected(self):
        self.edit(self.skill / 'SKILL.md', '../../../AGENTS.md', '/Users/example/AGENTS.md')
        self.rejected('nonportable link')

    def test_unregistered_skill_is_rejected(self):
        path = self.root / 'AGENTS.md'
        self.edit(path, '.agents/skills/chunkmaestro-dotnet/SKILL.md', 'docs/operations/development.md')
        self.rejected('missing AGENTS routing')

    def test_stale_routing_is_rejected(self):
        shutil.rmtree(self.skill)
        self.rejected('missing skill chunkmaestro-dotnet')

    def test_prompt_with_only_a_longer_skill_token_is_rejected(self):
        self.edit(self.skill / 'agents/openai.yaml', '$chunkmaestro-dotnet ', '$chunkmaestro-dotnet-other ')
        self.rejected('wrong skill')

    def test_duplicate_metadata_field_is_rejected(self):
        self.edit(self.skill / 'agents/openai.yaml', 'interface:\n', 'interface:\n  display_name: "Duplicate"\n')
        self.rejected('duplicate metadata field')

    def test_nonboolean_invocation_policy_is_rejected(self):
        self.edit(self.skill / 'agents/openai.yaml', 'allow_implicit_invocation: true', 'allow_implicit_invocation: "false"')
        self.rejected('policy must be a boolean')

    def test_explicit_only_policy_is_valid_when_intentionally_selected(self):
        self.edit(self.skill / 'agents/openai.yaml', 'allow_implicit_invocation: true', 'allow_implicit_invocation: false')
        self.assertEqual(check.validate(self.root), [])

    def test_yaml_tag_is_not_executed(self):
        self.edit(self.skill / 'SKILL.md', 'name: "chunkmaestro-dotnet"', 'name: !!python/object/apply:os.system ["false"]')
        self.rejected('quoted scalar')

    def test_invalid_utf8_is_rejected(self):
        (self.skill / 'SKILL.md').write_bytes(b'---\nname: "bad"\n---\n\xff')
        self.rejected('utf-8')

    def test_resource_symlink_is_rejected_before_its_contents_are_read(self):
        resource = self.skill / 'agents/openai.yaml'
        original = Path.is_symlink
        with mock.patch.object(Path, 'is_symlink', lambda path: path == resource or original(path)):
            self.rejected('symlink resource')

    def test_malformed_url_is_a_reported_failure(self):
        self.edit(self.skill / 'SKILL.md', '../../../AGENTS.md', 'https://[invalid')
        self.rejected('malformed link')

    def test_duplicate_frontmatter_field_is_rejected(self):
        self.edit(self.skill / 'SKILL.md', 'name: "chunkmaestro-dotnet"', 'name: "chunkmaestro-dotnet"\nname: "second"')
        self.rejected('duplicate or unsupported frontmatter field')

    def test_non_utf8_platform_default_does_not_change_validation(self):
        original = Path.read_text

        def cp1252_default(path, encoding=None, errors=None, **kwargs):
            return original(path, encoding=encoding or 'cp1252', errors=errors, **kwargs)

        with mock.patch.object(Path, 'read_text', cp1252_default):
            self.assertEqual(check.validate(self.root), [])


if __name__ == '__main__':
    unittest.main()
