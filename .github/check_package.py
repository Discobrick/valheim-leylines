"""Checks a Thunderstore package zip against https://wiki.thunderstore.io/mods/creating-a-package.
Usage: python check_package.py <package.zip> [expected-version]"""
import json, re, struct, sys, zipfile

zip_path = sys.argv[1]
expected = sys.argv[2] if len(sys.argv) > 2 else None
errors = []

with zipfile.ZipFile(zip_path) as z:
    names = z.namelist()
    for required in ("manifest.json", "icon.png", "README.md"):
        if required not in names:  # must be at the root, exact case
            errors.append(f"missing {required} at the zip root")

    if "manifest.json" in names:
        m = json.loads(z.read("manifest.json").decode("utf-8"))
        for key in ("name", "version_number", "website_url", "description", "dependencies"):
            if key not in m:
                errors.append(f"manifest.json: missing '{key}'")
        if not re.fullmatch(r"[A-Za-z0-9_]{1,128}", m.get("name", "")):
            errors.append("manifest.json: name must be 1-128 of a-z A-Z 0-9 _")
        if not re.fullmatch(r"\d+\.\d+\.\d+", m.get("version_number", "")):
            errors.append("manifest.json: version_number must be Major.Minor.Patch")
        if len(m.get("description", "")) > 250:
            errors.append(f"manifest.json: description is {len(m['description'])} chars, max 250")
        for dep in m.get("dependencies", []):
            if not re.fullmatch(r"[^-]+-[^-]+-\d+\.\d+\.\d+", dep):
                errors.append(f"manifest.json: dependency '{dep}' is not Team-Package-1.2.3")
        if expected and m.get("version_number") != expected:
            errors.append(f"manifest.json: version {m.get('version_number')} but the tag says {expected}")

    if "icon.png" in names:
        png = z.read("icon.png")
        if png[:8] != b"\x89PNG\r\n\x1a\n":
            errors.append("icon.png: not a PNG")
        elif struct.unpack(">II", png[16:24]) != (256, 256):
            errors.append(f"icon.png: {struct.unpack('>II', png[16:24])}, must be 256x256")

    if "README.md" in names:
        try:
            z.read("README.md").decode("utf-8")
        except UnicodeDecodeError:
            errors.append("README.md: not UTF-8")

for e in errors:
    print(f"::error::{e}")
if errors:
    sys.exit(1)
print(f"{zip_path}: valid Thunderstore package ({len(names)} files)")
