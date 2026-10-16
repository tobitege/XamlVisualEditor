# Debug inspection through CDP

The editor can expose itself through the Chrome DevTools Protocol without
moving the desktop pointer or sending keyboard input to the foreground application.
The endpoint is compiled in only when the MSBuild property `EnableCdpDiagnostics`
is `true`; it is `false` by default in every configuration, so regular Debug and
Release builds contain neither the endpoint nor its packages. Pass the property to
restore and build alike, for example:

```powershell
dotnet build .\src\XamlVisualEditor.App\XamlVisualEditor.App.csproj -c Debug -p:EnableCdpDiagnostics=true
```

A build with the property defines the compilation constant `CDP_DIAGNOSTICS` and
references `Chrome.DevTools.Avalonia` with its runtime dependencies.

## Starting the endpoint

Set `XVE_CDP_PORT` to an unused TCP port before starting the editor. An unset value
or `0` disables the endpoint. Invalid values are reported in the application log.

The endpoint permits inspection and mutation of the application, including C#
evaluation through the Runtime domain, so enable it only for a local development
session. It is protected as follows:

- It listens on `127.0.0.1` only.
- Every URL starts with a random token that is generated per process and written
  to the application log at startup, for example
  `CDP diagnostics listening at http://127.0.0.1:9222/<token>/json`. Requests
  without the token receive `403`.
- Requests that carry an `Origin` header other than `devtools://devtools` receive
  `403`, so web pages opened in a browser cannot connect.
- The CDP library's own listener has no request hook. It runs on an internal
  loopback port and refuses to create sessions; only the guarded endpoint creates
  them. WebDriver BiDi (`/session`) is not available.

Example in PowerShell 7, from the repository root, after a build with the property:

```powershell
$env:XVE_CDP_PORT = '9222'
$editor = '.\src\XamlVisualEditor.App\bin\Debug\net10.0\XamlVisualEditor.App.exe'
$project = 'C:\Projects\Sample\Sample.csproj'
Start-Process -FilePath $editor -ArgumentList @('--workspace', $project) -WindowStyle Hidden
```

Reserve a separate port for each concurrent session. Load the project before its
AXAML documents so that library metadata and dependency resolution are available.
The endpoint stops with the editor.

Take the token from the log line and discover the actual target ID before issuing
target-specific commands:

```powershell
$endpoint = 'http://127.0.0.1:9222/<token>/json'
Invoke-RestMethod -Uri $endpoint
```

The returned `webSocketDebuggerUrl` values already contain the token. Clients that
build URLs themselves from a host and port must be given the base
`http://127.0.0.1:9222/<token>`.

Use CDP DOM, Runtime, and Page commands for inspection, application-level actions,
and screenshots. A successful protocol response is not proof of a UI change; read
the resulting application state after each operation.

Server and client documentation: <https://github.com/wieslawsoltes/CDP>.
