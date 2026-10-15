using System;
using System.IO;
using Xunit;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Tests.Unit;

public sealed class SharedFileReaderTests
{
    [Fact]
    public void ReadAllText_ReadsFileThatAnotherHandleKeepsOpenForWriting()
    {
        string path = Path.Combine(Path.GetTempPath(), "shared-reader-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ \"Items\": [] }");
        try
        {
            using FileStream writer = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            Assert.Throws<IOException>(() => File.ReadAllText(path));
            Assert.Equal("{ \"Items\": [] }", SharedFileReader.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
