# EXE/DLL and Archive Previews (catalog plugins) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two official, non-built-in catalog plugins: `official.pe-info` (quick view and dashboard card for EXE and DLL files: headers, hardening, signature, manifest permissions, version info, imports/exports, .NET metadata, a resolved dependency tree) and `official.archive-info` (quick view and card for archives and zip-based packages: contents summary, warnings, package dependencies).

**Architecture:** Both are Python catalog plugins in `plugins-catalog/<name>/` (like `json-preview-python`), stdlib only, `fs.read` only. The PE plugin parses the file itself through ranged `c4k.read_bytes` calls behind a page cache with a read budget. The archive plugin parses nothing binary: it lists the archive through the app's own archive VFS (`fs.list` on `zip://<archive>`, `7z://…`, `tar://…`), so every format the app opens works, and reads package metadata files inside with `c4k.read_text` on `zip://<archive>::/<inner>`. No host (C#) code changes.

**Tech Stack:** Python ≥ 3.9 (`struct`, `xml.etree.ElementTree.fromstring`, `json`, `math`), the Python SDK client `plugins-sdk/python/c4k.py`, xunit.v3 tests in `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs` running the plugins against the real runtime.

**Spec:** the user's request (2026-10-31): "plugins that are not built in: an EXE info preview with as much as possible — dependencies, permissions etc.; a DLL info preview; an archive preview if possible". The decisions below make it concrete; there is no separate spec file.

## Design decisions

- **One PE plugin, two previews.** `exe` (`.exe .scr .cpl .efi`) and `dll` (`.dll .sys .ocx .drv .ax .winmd .mui`), both `priority: 55`, `card: true`. They share the parser. Only the order of sections differs: for an EXE, permissions and dependencies come first; for a DLL, exports do.
- **Only `fs.read`.** We don't verify signatures cryptographically, because that would need `shell.run` + `Get-AuthenticodeSignature`, a broader permission. The preview shows who signed the file, read from the PKCS#7 data, and always says "podpis nie został zweryfikowany kryptograficznie".
- **Dependency resolution** follows the Windows search order, simplified. A name is checked in this order:
  1. API set (`api-ms-*`, `ext-ms-*`) → "zestaw API systemu";
  2. KnownDLLs, read from `reg://HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs` with `c4k.list_dir`, lower-cased value names + `.dll` (if the list fails, an empty set);
  3. the file's own folder;
  4. `%SystemRoot%\System32`, or `SysWOW64` for an x86 image (`os.environ.get("SystemRoot", r"C:\Windows")`);
  5. otherwise "nie znaleziono" (warning).

  Each check is one `c4k.stat`. App-local DLLs, which are found in step 3, are parsed in turn (imports only), up to depth 3 and 60 modules in total. System DLLs are not walked.
- **.NET** (CLR data directory 14 is present) adds:
  - the runtime version string, plus the IL-only / 32-bit-required / 32-bit-preferred flags;
  - the `Assembly` row (name, version, culture);
  - every `AssemblyRef` row (name, version, public key token), each with where it comes from: the same folder → "obok"; else listed in `<name>.deps.json` → "pakiet"; else, when `<name>.runtimeconfig.json` names frameworks → "framework <name> <version>"; for .NET Framework images (no runtimeconfig) `mscorlib`/`System*` → "GAC / .NET Framework"; else warning;
  - the target framework, found by scanning the `#Blob` heap for `.NETCoreApp,Version=` / `.NETFramework,Version=` / `.NETStandard,Version=`.
- **The "permissions" section** combines several sources:
  - from the manifest (`RT_MANIFEST`, type 24): `requestedExecutionLevel` (`asInvoker` / `highestAvailable` / `requireAdministrator`) and `uiAccess`, plus `autoElevate`, `dpiAware`/`dpiAwareness`, `longPathAware`, `supportedOS` ids mapped to Windows names, and `dependentAssembly` names;
  - from the headers: `DllCharacteristics` (ASLR, high-entropy VA, DEP/NX, CFG, `FORCE_INTEGRITY`, `APPCONTAINER`, `NO_SEH`) and `IMAGE_FILE_LARGE_ADDRESS_AWARE`.
- **"Uwagi" (notes) are facts, never a verdict.** They cover:
  - no embedded signature, which adds the hint that Windows files are often signed by a catalog, which this plugin does not check;
  - ASLR/DEP/CFG off;
  - a section that is both writable and executable;
  - section entropy > 7.2 or UPX/ASPack/Themida section names → "prawdopodobnie spakowany";
  - TLS callbacks;
  - an overlay (data after the last section), with its kind sniffed: `PK\x03\x04` ZIP, `7z\xBC\xAF\x27\x1C` 7-Zip, `Nullsoft`, `Inno Setup`;
  - `requireAdministrator` or `uiAccess=true`;
  - imports from the sensitive-API list `VirtualAllocEx, WriteProcessMemory, CreateRemoteThread, SetWindowsHookExA/W, NtUnmapViewOfSection, URLDownloadToFileA/W, AdjustTokenPrivileges, IsDebuggerPresent`, shown as "funkcje wrażliwe (informacyjnie)".
- **Read limits for the PE plugin.** Pages are 64 KB and are fetched with `c4k.read_bytes(path, offset, 65536)`. A render has a budget of 8 MB. Entropy uses at most the first 256 KB of each section. At most 400 imported functions per DLL and 2000 exports are shown, and the rest are counted ("… i jeszcze N"). When a read falls outside the file or over the budget, the section in progress ends with a "⚠" line and the other sections are still shown. Nothing raises out of `preview.render`.
- **Archive plugin** `official.archive-info`. Preview `archive` handles `.zip .7z .rar .tar .gz .tgz .tbz2 .txz .jar .nupkg .vsix .whl` (`.tar.gz` etc. match through `.gz`). It uses `priority: 55`, `card: true`. The scheme table is a copy of `ArchiveVfsPlugin.Formats`, longest extension first. Listing is breadth-first through `c4k.list_dir`, capped at 20 000 entries / 2 000 folders, with "lista niepełna" when the caps are hit. The preview shows:
  - totals (files, folders, uncompressed size, the archive file's size, the ratio);
  - the top-level tree (two levels, at most 200 lines);
  - the 15 largest files;
  - a breakdown by extension (top 10);
  - the root `README*` (first 40 lines, read with `read_text(max_bytes=16384)`);
  - a package section:
    - `.nupkg`: `*.nuspec` at the root gives id, version, authors, and dependencies per `targetFramework` group;
    - `.whl`: `*.dist-info/METADATA` gives Name, Version, `Requires-Python`, `Requires-Dist`;
    - `.jar`: `META-INF/MANIFEST.MF` gives `Main-Class`, `Implementation-Version`, `Class-Path`;
    - `.vsix`: `extension.vsixmanifest` gives Identity Id/Version/Publisher and `Dependency` ids.

  Its notes:
  - executable content (`.exe .dll .msi .bat .cmd .ps1 .vbs .js .lnk .scr .hta`);
  - a double extension where the last one is executable (`faktura.pdf.exe`);
  - a ratio > 100 with uncompressed > 1 GB → "możliwa bomba zip";
  - the archive could not be opened, i.e. `list_dir` raised (e.g. an encrypted 7z): the message from the app, no traceback.
- **XML safety.** Manifest/nuspec/vsixmanifest text containing `<!DOCTYPE` or `<!ENTITY` is not parsed; the plugin notes "pominięto: DTD w XML". Only `ET.fromstring` is used, never `ET.parse(path)`, because `PluginSourceCheck` must stay clean and files are read through the SDK.
- Output text is Polish, like the other official plugins. Plugins are not translated, so nothing goes into `en.json`.

## Global Constraints

- Python plugins import only the stdlib and `c4k`. No `open()`, `io.open`, `subprocess`, `print`, or `sys.stdout` writes, so that `PluginSourceCheck.ScanFolder` reports nothing (`FolderConfigTests` scans every catalog folder).
- `c4k.py` in each new folder is a byte-identical copy of `plugins-sdk/python/c4k.py` (test `Example_sdk_clients_match_plugins_sdk`).
- `plugin.json` has `"$schema": "https://raw.githubusercontent.com/4kprojekt/Commander4K/main/docs/plugin.schema.json"`, `"author": "4Kprojekt"`, `"version": "1.0.0"`, `"runtime": { "type": "python", "entry": "main.py", "version": ">=3.9" }`, `"permissions": ["fs.read"]`.
- After any change in a catalog folder, run `pwsh ./plugins-catalog/Update-Catalog.ps1` and commit `catalog.json`, otherwise `PluginCatalogTests` fails. The app's embedded copy is the same file (`Built_in_copy_is_the_repository_catalog`).
- `dotnet build Commander4K.sln` has to pass with zero warnings (`TreatWarningsAsErrors`).
- Keep the `Co-Authored-By` trailer in commits.

## Review Focus

1. **A truncated or hostile PE**: a cut-off file, RVAs pointing outside the image, import name chains that loop. The user expects the headers that are readable plus "⚠ … uszkodzony", never a JSON-RPC error or a hang. Tests: `Pe_preview_survives_broken_import_table` and `Pe_preview_of_non_pe_file` (Task 2). The loop guard: stop an import thunk/name walk after 4096 steps.
2. **A huge EXE on a slow share/SFTP**: here `read_bytes` with an offset may stream through the file (`SkipAsync`). The user expects a bounded wait, so the 8 MB budget counts *offset + length* for non-local paths (anything not matching `^[A-Za-z]:\\`). Test: `Pe_preview_respects_read_budget` (Task 2) uses a fake large overlay.
3. **An x86 EXE on 64-bit Windows** must resolve dependencies in `SysWOW64`, not `System32`, or every DLL looks wrong. Test: `Dependency_folder_for_machine` (Task 3), a Python-side unit run via `python -I -c` from the test.
4. **`.tar.gz` / `.tgz` scheme**: the plugin must build `tar://`, not `gz://`, or the listing is one file. Test: `Archive_preview_lists_tar_gz` (Task 5).
5. **An archive with thousands of entries** must finish under the caps and say the list is incomplete instead of timing out. Test: `Archive_preview_caps_large_listing` (Task 5).

---

## File Structure

```
plugins-catalog/pe-info-python/
  plugin.json      manifest (two previews)
  c4k.py           SDK copy
  main.py          handlers preview.render / preview.card, Markdown and card building
  pe.py            RangeReader (page cache + budget), headers, sections, imports, delay imports, exports, TLS, overlay
  resources.py     resource tree walk, VS_VERSIONINFO, RT_MANIFEST parsing
  clr.py           CLR header, metadata streams, Assembly/AssemblyRef tables, target framework scan
  signature.py     WIN_CERTIFICATE + minimal DER reader → signer/issuer/serial/signing time
  deps.py          dependency resolution (API sets, KnownDLLs, folder, System32/SysWOW64) and the app-local tree
plugins-catalog/archive-info-python/
  plugin.json, c4k.py
  main.py          handlers, listing walk, summary, notes, card
  packages.py      nuspec / METADATA / MANIFEST.MF / vsixmanifest readers
plugins-catalog/catalog.json        two entries (+ hashes by script)
tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs   tests + helper for existing files / bytes
docs/plugins.md, CLAUDE.md          list the new catalog plugins
```

---

### Task 1: PE reader, headers, sections, imports/exports, EXE preview skeleton

**Files:**
- Create: `plugins-catalog/pe-info-python/{plugin.json,c4k.py,main.py,pe.py}`
- Modify: `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs` (helper + tests; add the folder to `Example_sdk_clients_match_plugins_sdk`)
- Modify: `plugins-catalog/catalog.json` (entry; hashes by script)

**Interfaces:**
- Produces (Python, `pe.py`):
  - `class RangeReader(path: str, size: int, budget: int = 8 * 1024 * 1024)` with `read(offset: int, length: int) -> bytes`. It raises `ReadLimit` past the budget or EOF.
  - `class PeError(Exception)`
  - `parse(reader: RangeReader) -> PeImage`, where `PeImage` is a dataclass with these fields: `machine: str` (`"x86"|"x64"|"ARM64"|"ARM64EC"|"ARM"|hex`), `is_dll: bool`, `pe32_plus: bool`, `subsystem: str`, `timestamp: int`, `linker: str`, `os_version: str`, `image_base: int`, `entry_rva: int`, `characteristics: int`, `dll_characteristics: int`, `sections: list[Section]` (name, rva, vsize, raw_offset, raw_size, flags, entropy: float|None), `data_dirs: list[tuple[int,int]]`, `imports: list[Import]` (dll, functions: list[str], delayed: bool), `exports: Exports|None` (dll_name, items: list[(ordinal, name|None, forwarder|None)]), `tls_callbacks: int`, `overlay: tuple[int,int]|None`, `warnings: list[str]`.
  - `rva_to_offset(image, rva) -> int|None`
- Produces (C# test helper): `private async Task<(ExternalPreviewPlugin Preview, IPluginHost Host, VfsEntry Entry)> CatalogPreviewOfAsync(string folder, string path, string? previewId = null)`, which builds the `VfsEntry` from an existing file.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Pe_preview_reads_native_exe_headers_and_imports()
{
    var exe = Path.ChangeExtension(typeof(ExternalPluginTests).Assembly.Location, ".exe"); // MTP apphost
    var (preview, host, entry) = await CatalogPreviewOfAsync("pe-info-python", exe, "exe");
    var md = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken)).Markdown;
    Assert.Contains(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ARM64" : "x64", md, StringComparison.Ordinal);
    Assert.Contains("konsola", md, StringComparison.Ordinal);            // subsystem
    Assert.Contains("KERNEL32.dll", md, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("ASLR: ✅", md, StringComparison.Ordinal);
}

[Fact]
public async Task Pe_preview_lists_dll_exports_and_forwarders()
{
    var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
    var (preview, host, entry) = await CatalogPreviewOfAsync("pe-info-python", kernel32, "dll");
    var md = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken)).Markdown;
    Assert.Contains("## Eksporty", md, StringComparison.Ordinal);
    Assert.Contains("CreateFileW", md, StringComparison.Ordinal);
    Assert.Contains("→", md, StringComparison.Ordinal);                  // at least one forwarder
}

[Fact]
public async Task Pe_preview_of_non_pe_file()   // bytes "MZ" + 0x3C..: e_lfanew = 0x7FFFFFF0
{ /* write 128 bytes, expect MarkdownPreview containing "nie jest prawidłowym plikiem PE" */ }

[Fact]
public async Task Pe_preview_survives_broken_import_table()
{ /* copy the apphost, set data directory 1 RVA (offset e_lfanew+24+ (pe32+ ? 112 : 96) + 8) to 0x7FFFFFF0;
     expect "## Nagłówek" present and "⚠" + "tabela importów" present */ }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --solution Commander4K.sln -- --filter-method "*Pe_preview*"`
Expected: FAIL (folder `plugins-catalog/pe-info-python` missing).

- [ ] **Step 3: Implement `pe.py` and `main.py`**

`main.py` registers `preview.render`, reads `params["preview"]` (`"exe"`/`"dll"`), and returns `{"kind": "markdown", "content": …}`. The sections are `# <name>`, then `## Nagłówek` (a table: architektura, typ, podsystem, czas kompilacji UTC or "deterministyczny (hash)" when the timestamp is in the future or before 1990, linker, minimalny Windows, punkt wejścia), then `## Zabezpieczenia` (`ASLR: ✅/❌`, Wysoka entropia VA, DEP/NX, CFG, Integralność wymuszona, AppContainer, Large address aware), then `## Sekcje` (a table with entropy to 2 decimals and flags `R W X`), then `## Importy` (one bullet per DLL with a function count, and the functions in a `<details>`-free collapsed list: the first 400, then "… i jeszcze N"), then `## Eksporty` (first in the DLL preview), then `## Uwagi`. The subsystem names are: 2 → "GUI Windows", 3 → "konsola", 1 → "natywny (sterownik)", 10–13 → "EFI", 14 → "Xbox", 16 → "aplikacja rozruchowa". Each parse step is wrapped so that `ReadLimit`/`PeError`/`struct.error` become a warning, and later sections still render. A file that is not PE at all returns only the title + "⚠ To nie jest prawidłowym plikiem PE (…)". Write `plugin.json` with both previews, copy `c4k.py`, add the catalog entry (`"name": "Podgląd EXE i DLL"`, pl/en descriptions, tags `exe, dll, pe, zależności, dependencies, podpis, signature`, `"example": "{ \"preview\": { \"view\": \"dashboard\", \"tiles\": [ { \"title\": \"Program\", \"open\": \"app.exe\", \"card\": true } ] } }"`), and run `pwsh ./plugins-catalog/Update-Catalog.ps1`. Add the `[InlineData("../../plugins-catalog/pe-info-python", "python", "c4k.py")]` row.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --solution Commander4K.sln -- --filter-method "*Pe_preview*" --filter-method "*sdk_clients*" --filter-class "*FolderConfigTests*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add plugins-catalog/pe-info-python plugins-catalog/catalog.json tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs
git commit -m "Add official.pe-info catalog plugin: PE headers, sections, imports, exports"
```

### Task 2: Resources: version info, manifest permissions, notes, read budget

**Files:**
- Create: `plugins-catalog/pe-info-python/resources.py`
- Modify: `plugins-catalog/pe-info-python/main.py`, `pe.py` (overlay sniffing, TLS, sensitive APIs)
- Test: `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs`

**Interfaces:**
- Consumes: `RangeReader`, `PeImage`, `rva_to_offset` (Task 1).
- Produces: `version_info(reader, image) -> dict[str, str]` (keys `CompanyName, FileDescription, FileVersion, ProductName, ProductVersion, OriginalFilename, InternalName, LegalCopyright`, plus `"_fixed"` = the `x.y.z.w` from `VS_FIXEDFILEINFO`); `manifest(reader, image) -> Manifest|None` (fields `xml: str`, `execution_level: str|None`, `ui_access: bool`, `auto_elevate: bool`, `dpi: str|None`, `long_path_aware: bool`, `supported_os: list[str]`, `dependencies: list[str]`, `skipped_reason: str|None`); `notes(image, manifest, signature) -> list[str]`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Pe_preview_shows_version_and_execution_level()
{
    var regedit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe");
    var md = await RenderAsync("pe-info-python", regedit, "exe");
    Assert.Contains("## Uprawnienia", md, StringComparison.Ordinal);
    Assert.Contains("highestAvailable", md, StringComparison.Ordinal);
    Assert.Contains("Microsoft Corporation", md, StringComparison.Ordinal);   // CompanyName
    Assert.Contains("## Wersja", md, StringComparison.Ordinal);
}

[Fact]
public async Task Pe_preview_respects_read_budget()
{ /* apphost bytes + 40 MB of zero overlay written to the test folder; Stopwatch < 20 s;
     md contains "Dane doklejone" and "40,0 MB" (overlay size formatted with one decimal, Polish comma) */ }
```

`RenderAsync(folder, path, previewId)` is a small wrapper around `CatalogPreviewOfAsync` + `LoadAsync` returning the Markdown.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --solution Commander4K.sln -- --filter-method "*Pe_preview_shows_version*" --filter-method "*read_budget*"`
Expected: FAIL (no `## Uprawnienia`).

- [ ] **Step 3: Implement `resources.py`, the notes, and rendering**

The resource walk goes from data directory 2: type → name/id → language, at most 3 levels, and at most 512 entries per directory (loop guard). Version info means `RT_VERSION` (16), the first language. Parse `StringFileInfo` keys as UTF-16LE, aligned to 4. The manifest is `RT_MANIFEST` (24): decode UTF-8 (strip the BOM), apply the DTD guard from the design decisions, then `ET.fromstring`. Match tags by local name, ignoring namespaces. Map the `supportedOS` GUIDs as follows: `{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}` Windows 10/11, `{1f676c76-80e1-4239-95bb-83d0f6d0da78}` 8.1, `{4a2f28e3-53b9-4441-ba9c-d69d4a4a6e38}` 8, `{35138b9a-5d96-4fbd-8e2d-a2440225f93a}` 7, `{e2011457-1546-43c5-a5fe-008deee3d3f0}` Vista. The EXE preview orders its sections as `## Uprawnienia` (execution level with a Polish gloss: asInvoker → "uprawnienia użytkownika", highestAvailable → "najwyższe dostępne (administrator pyta o UAC)", requireAdministrator → "wymaga administratora (UAC)", missing → "brak manifestu — uprawnienia użytkownika, wirtualizacja UAC dla starszych programów"), `## Wersja`, then the Task 1 sections. Sniff the overlay using the first 16 bytes. The size format is `f"{n/1048576:.1f} MB".replace(".", ",")`. For a non-local path, `RangeReader` charges the budget with `offset + length` (Review Focus 2).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --solution Commander4K.sln -- --filter-method "*Pe_preview*"`
Expected: PASS.

- [ ] **Step 5: Run the catalog script and commit**

```bash
pwsh ./plugins-catalog/Update-Catalog.ps1
git add plugins-catalog tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs
git commit -m "pe-info: version info, manifest permissions, notes, read budget"
```

### Task 3: Dependency resolution and the app-local tree

**Files:**
- Create: `plugins-catalog/pe-info-python/deps.py`
- Modify: `plugins-catalog/pe-info-python/main.py`
- Test: `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs`

**Interfaces:**
- Consumes: `parse`, `RangeReader` (Task 1).
- Produces: `system_folder(machine: str, system_root: str) -> str` (`"x86"` → `SysWOW64`, else `System32`); `resolve(name: str, folder: str, machine: str, known: set[str]) -> Resolution` (fields `name, kind: "apiset"|"known"|"local"|"system"|"missing", path: str|None`); `tree(path: str, image: PeImage, max_depth: int = 3, max_modules: int = 60) -> list[Node]` (each `Node` has `resolution, depth, children_count, error: str|None`; modules already seen are not repeated); `known_dlls() -> set[str]`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("x86", @"C:\Windows", @"C:\Windows\SysWOW64")]
[InlineData("x64", @"C:\Windows", @"C:\Windows\System32")]
[InlineData("ARM64", @"C:\Windows", @"C:\Windows\System32")]
public async Task Dependency_folder_for_machine(string machine, string root, string expected)
{ /* run `python -I -c "import sys; sys.path.insert(0, r'<folder>'); import deps; sys.stdout.write(deps.system_folder('<m>', r'<root>'))"`
     via the detected python from _runtimes; Assert.Equal(expected, stdout) */ }

[Fact]
public async Task Pe_preview_resolves_dependencies()
{ /* render the apphost unchanged: md contains "## Zależności"; the KERNEL32.dll line contains
     "KnownDLLs" or "System32"; every api-ms-win-* line contains "zestaw API"; no "❌" line */ }

[Fact]
public async Task Pe_preview_marks_missing_local_dependency()
{ /* copy the apphost to the test folder as app.exe and patch the first imported DLL name string in place
     (same length, e.g. "KERNEL32.dll" → "KERNEQ32.dll"); md contains "❌ KERNEQ32.dll — nie znaleziono"
     and the name under "## Uwagi" */ }
```

(`deps.py` imports `c4k` only inside the functions that call it, so `system_folder` can be imported without a running host.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --solution Commander4K.sln -- --filter-method "*Dependency_folder*" --filter-method "*resolves_dependencies*"`
Expected: FAIL.

- [ ] **Step 3: Implement `deps.py` and the `## Zależności` section**

The section is a nested bullet list: `- ✅ KERNEL32.dll — znana systemowa (KnownDLLs)`, `- ✅ VCRUNTIME140.dll — obok pliku` (+ children), `- ℹ️ api-ms-win-crt-runtime-l1-1-0.dll — zestaw API systemu`, `- ❌ foo.dll — nie znaleziono` (also added to `## Uwagi`). Delay-loaded imports are marked "(opóźnione)" and are never "missing" warnings, only info. In the EXE preview the section comes right after `## Uprawnienia`; in the DLL preview, after `## Eksporty`. The card (Task 6) uses the count of missing DLLs.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --solution Commander4K.sln -- --filter-method "*Pe_preview*" --filter-method "*Dependency_folder*"`
Expected: PASS.

- [ ] **Step 5: Catalog script + commit** (`pe-info: resolve dependencies and app-local tree`)

### Task 4: .NET metadata and the Authenticode signer

**Files:**
- Create: `plugins-catalog/pe-info-python/clr.py`, `plugins-catalog/pe-info-python/signature.py`
- Modify: `plugins-catalog/pe-info-python/main.py`
- Test: `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs`

**Interfaces:**
- Consumes: `RangeReader`, `PeImage`, `rva_to_offset` (Task 1); `deps.resolve`-style folder lookup via `c4k.stat`.
- Produces: `clr_info(reader, image) -> Clr|None` (fields `runtime: str`, `flags: list[str]`, `assembly: (name, version, culture)|None`, `references: list[(name, version, token_hex)]`, `target_framework: str|None`); `resolve_assembly(ref_name: str, folder: str, base: str, runtimeconfig: dict|None, deps: dict|None) -> (kind, detail)`; `signer(reader, image) -> Signature` (fields `present: bool`, `subject: str|None`, `organization: str|None`, `issuer: str|None`, `serial: str|None`, `not_before/not_after: str|None`, `signing_time: str|None`, `timestamped: bool`, `error: str|None`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Pe_preview_reads_dotnet_assembly_references()
{
    var dll = typeof(ExternalPluginTests).Assembly.Location;  // Commander4K.Plugins.Tests.dll
    var md = await RenderAsync("pe-info-python", dll, "dll");
    Assert.Contains("## .NET", md, StringComparison.Ordinal);
    Assert.Contains(".NETCoreApp,Version=v10.0", md, StringComparison.Ordinal);
    Assert.Contains("Commander4K.Core", md, StringComparison.Ordinal);
    Assert.Contains("obok pliku", md, StringComparison.Ordinal);
    Assert.Contains("Microsoft.NETCore.App", md, StringComparison.Ordinal);  // from the runtimeconfig.json next to the apphost dll
}

[Fact]
public async Task Pe_preview_shows_embedded_signer()
{
    var coreclr = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");
    var md = await RenderAsync("pe-info-python", coreclr, "dll");
    Assert.Contains("## Podpis", md, StringComparison.Ordinal);
    Assert.Contains("Microsoft Corporation", md, StringComparison.Ordinal);
    Assert.Contains("nie został zweryfikowany kryptograficznie", md, StringComparison.Ordinal);
}

[Fact]
public async Task Pe_preview_explains_missing_embedded_signature()  // kernel32.dll is catalog-signed
{ /* md contains "brak osadzonego podpisu" and "katalog" */ }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --solution Commander4K.sln -- --filter-method "*dotnet_assembly*" --filter-method "*signer*" --filter-method "*embedded_signature*"`
Expected: FAIL.

- [ ] **Step 3: Implement `clr.py`**

These are the parts that the signatures do not determine:
- In the `#~` stream, the 64-bit `valid` mask gives the row counts in table order. The heap index sizes come from `HeapSizes` bits 0/1/2 (strings/guid/blob → 4 bytes when set).
- Coded-index sizes follow ECMA-335 II.24.2.6: the tag bits are `ceil(log2(n))`, and an index is 2 bytes if the largest referenced table has fewer than `2^(16-tagbits)` rows, else 4.
- Compute the row size of every table from `0x00` to `0x23` from the ECMA-335 column lists, so that you can skip to `Assembly` (0x20) and `AssemblyRef` (0x23).
- If an unknown table bit below 0x23 is set, return `references=[]` with a warning instead of guessing.
- The target framework is a byte search in `#Blob` for the three prefixes; read up to the first non-printable byte.
- `runtimeconfig.json` and `deps.json` sit next to the file as `<base>.runtimeconfig.json` / `<base>.deps.json` and are read with `c4k.read_text` (max 1 MB) and `json.loads`.

- [ ] **Step 4: Implement `signature.py`**

Data directory 4 holds a *file offset*, not an RVA. Read `WIN_CERTIFICATE` (`dwLength`, `wRevision`, `wCertificateType` = 2 PKCS#7). The DER reader is a recursive TLV with length forms up to 4 bytes and depth ≤ 32. The path is `ContentInfo → [0] SignedData → certificates [0]` and `signerInfos`. The signer is the certificate whose issuer+serial match `signerInfos[0].issuerAndSerialNumber`. Names come from OIDs `2.5.4.3` CN and `2.5.4.10` O. The signing time is auth attribute `1.2.840.113549.1.9.5` (UTCTime/GeneralizedTime). Set `timestamped` when the unauthenticated attributes contain `1.2.840.113549.1.9.6` or `1.3.6.1.4.1.311.3.3.1`. Any DER error becomes `error` and is shown as "⚠ nie udało się odczytać podpisu".

- [ ] **Step 5: Render `## .NET` and `## Podpis`**

`## Podpis` comes right after `## Uprawnienia` in the EXE preview. `## .NET` follows `## Nagłówek` and lists references with ✅/ℹ️/❌ as in Task 3.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --solution Commander4K.sln -- --filter-method "*Pe_preview*"`
Expected: PASS.

- [ ] **Step 7: Catalog script + commit** (`pe-info: .NET metadata and Authenticode signer`)

### Task 5: Archive preview plugin

**Files:**
- Create: `plugins-catalog/archive-info-python/{plugin.json,c4k.py,main.py,packages.py}`
- Modify: `plugins-catalog/catalog.json`, `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs` (+ SDK-copy row)

**Interfaces:**
- Produces: `archive_root(path: str) -> str` (`f"{scheme}://{path}"`, using the copied `FORMATS` table, longest extension first); `walk(root: str, max_entries=20000, max_dirs=2000) -> Listing` (fields `files: list[entry]`, `dirs: int`, `truncated: bool`, `error: str|None`); `inner(root: str, relative: str) -> str` (`f"{root}::/{relative}"`); in `packages.py`, `nuspec(xml) / wheel_metadata(text) / jar_manifest(text) / vsix(xml) -> list[tuple[str, str]] + dependencies: list[str]`.

- [ ] **Step 1: Write the failing tests** (build the archives in the test folder with `System.IO.Compression.ZipFile` / `System.Formats.Tar.TarFile` + `GZipStream`)

```csharp
[Fact] public async Task Archive_preview_summarises_zip()
// entries: readme.md ("# Witaj\nopis"), src/a.cs, src/b.cs, faktura.pdf.exe
// md: "Pliki: 4", "Foldery: 1", "# Witaj" (README section), "faktura.pdf.exe" under "## Uwagi", ".cs" in the extension table

[Fact] public async Task Archive_preview_lists_tar_gz()      // data.tar.gz with x/y.txt → md contains "y.txt"

[Fact] public async Task Archive_preview_reads_nuspec_dependencies()
// Demo.1.0.0.nupkg with Demo.nuspec: <dependencies><group targetFramework="net8.0"><dependency id="Newtonsoft.Json" version="13.0.1"/></group></dependencies>
// md: "## Pakiet NuGet", "Newtonsoft.Json", "13.0.1", "net8.0"

[Fact] public async Task Archive_preview_reads_wheel_and_jar()
// demo-1.0-py3-none-any.whl with demo-1.0.dist-info/METADATA "Requires-Dist: requests>=2"; app.jar with MANIFEST.MF "Main-Class: pl.demo.App"

[Fact] public async Task Archive_preview_skips_xml_with_dtd()   // nuspec starting with <!DOCTYPE → "pominięto: DTD w XML"

[Fact] public async Task Archive_preview_caps_large_listing()
// 2 500 folders × 1 file; md contains "lista niepełna"; completes (default test timeout)

[Fact] public async Task Archive_preview_of_broken_zip()     // "PK\x03\x04" + junk → md contains "⚠" and no "Traceback"
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --solution Commander4K.sln -- --filter-method "*Archive_preview*"`
Expected: FAIL (folder missing).

- [ ] **Step 3: Implement**

`main.py` renders the sections `# <name>`, `## Podsumowanie`, `## Zawartość` (tree), `## Największe pliki`, `## Typy plików`, `## README`, `## Pakiet NuGet|Python (wheel)|Java (JAR)|Rozszerzenie VS Code/VS`, and `## Uwagi`, as listed in the design decisions. Sizes are in the same Polish MB/KB format as Task 2 (copy the 6-line helper; the plugins do not share modules). Package files are found among the root listing, or the `*.dist-info/` folder for wheels. Write the manifest (`"name": "Podgląd archiwów"`, tags `zip, 7z, rar, tar, archiwum, archive, nupkg, jar, whl`, `"example": "{ \"preview\": { \"view\": \"dashboard\", \"tiles\": [ { \"title\": \"Paczka\", \"open\": \"release.zip\", \"card\": true } ] } }"`), copy `c4k.py`, and run the catalog script.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --solution Commander4K.sln -- --filter-method "*Archive_preview*" --filter-method "*sdk_clients*" --filter-class "*FolderConfigTests*"`
Expected: PASS.

- [ ] **Step 5: Commit** (`Add official.archive-info catalog plugin`)

### Task 6: Dashboard cards for both plugins, docs

**Files:**
- Modify: `plugins-catalog/pe-info-python/main.py`, `plugins-catalog/archive-info-python/main.py`
- Modify: `docs/plugins.md` (line "The official catalog has preview plugins with cards: …"), `CLAUDE.md` ("Catalog preview plugins with cards" bullet), and `README.md` if it lists catalog plugins
- Test: `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs`

**Interfaces:**
- Consumes: the analysis functions from Tasks 1–5. The card handlers reuse them with a smaller budget (2 MB for PE: no entropy, no tree, direct imports only).
- Produces: a `preview.card` handler in each plugin.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Pe_card_shows_architecture_signature_and_level()
{ /* regedit.exe via LoadCardAsync(new PreviewCardRequest(entry, null, null), host, TestToken):
     CardValue labels "Architektura", "Wersja", "Podpis", "Uprawnienia" present;
     "Uprawnienia" value "najwyższe dostępne"; Status Ok; an action with Target == entry.Path */ }

[Fact]
public async Task Archive_card_shows_counts_and_warning_status()
{ /* zip with faktura.pdf.exe: "Pliki" == "1", Status == CardStatus.Warning */ }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --solution Commander4K.sln -- --filter-method "*_card_*"`
Expected: FAIL.

- [ ] **Step 3: Implement the cards**

PE card: title = `FileDescription` or the file name; subtitle = `CompanyName`; items `Architektura`, `Wersja` (`_fixed`), `Podpis` (organisation, or "brak"), `Uprawnienia`, `Brakujące DLL` (only when > 0, status error); status `warning` if unsigned or with missing DLLs, `error` if not PE; action `{label: "Pokaż szczegóły", target: path}`. Archive card: `Pliki`, `Rozmiar po rozpakowaniu`, `Stopień kompresji`, the package id/version when one is present; status `warning` when there are any notes; action "Otwórz archiwum" → the `archive_root` path (Enter-like). Update the docs lines to list `official.pe-info` and `official.archive-info`. Run the catalog script.

- [ ] **Step 4: Full verification**

Run: `dotnet build Commander4K.sln` → 0 warnings, then `dotnet test --solution Commander4K.sln`
Expected: all pass (including `PluginCatalogTests`, `LocalizationTests`, the source-check scan).

- [ ] **Step 5: Manual check in the app**

In the app (`dotnet run --project src/Commander4K.App`), install both plugins from the local catalog folder through the plugin manager, approve `fs.read`, then use Ctrl+Q on `C:\Windows\regedit.exe`, `C:\Windows\System32\kernel32.dll`, and a `.zip`/`.nupkg`. Confirm the Markdown tables render and that the cursor moves without stalls. (A catalog install from GitHub works only after the files are pushed to `main`.)

- [ ] **Step 6: Commit** (`pe-info, archive-info: dashboard cards; docs`)
