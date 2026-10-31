# Commander4K plugin SDK

These are open-source (MIT) clients for the Commander4K plugin protocol. Each one is a single file with no dependencies. Copy the one for your language into your plugin folder:

| Language | File |
|---|---|
| Python ≥ 3.9 | [`python/c4k.py`](python/c4k.py) |
| Node.js ≥ 18 | [`node/c4k.js`](node/c4k.js) |
| PowerShell 7 | [`powershell/c4k.ps1`](powershell/c4k.ps1) |

The protocol, every SDK function in all three languages, the permissions and the manifest are all in [`docs/plugins.md`](../docs/plugins.md). Working examples are in [`examples/plugins`](../examples/plugins).

**Rule:** a plugin does all file, process, network and system work through the SDK, never through its language's own I/O, and it never writes to stdout. The app checks permissions on every SDK call and writes each one to the audit log. Its installer and plugin manager point out code that bypasses the SDK.
