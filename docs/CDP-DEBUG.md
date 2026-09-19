# Debug inspection through CDP

Debug builds can expose the editor through the Chrome DevTools Protocol without
moving the desktop pointer or sending keyboard input to the foreground application.
The server is disabled by default and is not included in Release builds.

Set `XVE_CDP_PORT` to an unused TCP port before starting the editor. An unset value
or `0` disables the server. Invalid values are reported in the application log.
The server accepts loopback connections only. It permits inspection and mutation
of the application, so enable it only for a local development session.

Example in PowerShell 7, from the repository root, after a Debug build:

```powershell
$env:XVE_CDP_PORT = '9222'
$editor = '.\src\XamlVisualEditor.App\bin\Debug\net10.0\XamlVisualEditor.App.exe'
$project = 'C:\Projects\Sample\Sample.csproj'
Start-Process -FilePath $editor -ArgumentList @('--workspace', $project) -WindowStyle Hidden
```

Reserve a separate port for each concurrent session. Load the project before its
AXAML documents so that library metadata and dependency resolution are available.
The server stops with the editor.

Discover the actual target ID before issuing target-specific commands:

```powershell
$endpoint = 'http://127.0.0.1:9222/json'
Invoke-RestMethod -Uri $endpoint
cdp-cli list-targets --host http://127.0.0.1:9222
```

Use CDP DOM, Runtime, and Page commands for inspection, application-level actions,
and screenshots. Do not use desktop activation or OS-input automation when the
workstation is in use. A successful protocol response is not proof of a UI change;
read the resulting application state after each operation.

Server and client documentation: <https://github.com/wieslawsoltes/CDP>.
