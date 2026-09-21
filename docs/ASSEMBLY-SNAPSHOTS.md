# Local assembly snapshots

Use an assembly snapshot when another process builds the workspace while the editor is open.
Sources and project files stay in their original location. Runtime assemblies come from a separate directory.

Create `%APPDATA%\XamlVisualEditor\assembly-snapshots.json` on Windows:

```json
[
  {
    "workspaceRoot": "C:\\src\\SampleApp",
    "sourceDirectory": "C:\\src\\SampleApp\\build\\Debug",
    "snapshotDirectory": "D:\\preview-snapshots\\SampleApp",
    "previewApplication": "SampleApp.dll"
  }
]
```

Copy the output DLLs, PDBs, dependency manifests and runtime configuration files into the snapshot.
Preserve subdirectories, including native libraries and resource assemblies. Copy only from a stable build output.

The matching workspace uses the snapshot as its MSBuild output and reference directory.
Assembly resolution searches the snapshot before project hint paths.
The editor resolves output assemblies and available assembly references from that snapshot.
C# workspace initialization rejects references that still point into the live output directory.
Missing snapshot assemblies cause an error; the previewer does not rebuild the live workspace automatically.
Assembly discovery stays within the snapshot even when the project output is missing.

`previewApplication` is optional. Set it to an application assembly within the snapshot
when more than one Avalonia application is available. It applies only to the matching workspace.
An explicit `XVE_PREVIEWER_APP_ASSEMBLY` override still takes precedence and is redirected into the snapshot.
The selected application must include its dependency manifest and runtime configuration.
An incomplete selection reports an error instead of choosing another application.

Close XVE normally before refreshing the snapshot. Reopen the workspace after copying the files.
Snapshots are not refreshed automatically. Keep this machine-local configuration out of source control.
