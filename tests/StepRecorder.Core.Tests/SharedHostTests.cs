namespace StepRecorder.Core.Tests;

public class SharedHostTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32\ApplicationFrameHost.exe")]
    [InlineData(@"C:\Program Files\Java\bin\JAVAW.EXE")]
    [InlineData(@"C:\tools\dotnet.exe")]
    [InlineData(@"C:\Python\python3.12.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    public void 共通のホストは同じexeでも別のアプリ(string path) => Assert.True(SharedHost.Is(path));

    [Theory]
    [InlineData(@"C:\Program Files (x86)\sakura\sakura.exe")]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"C:\app\MyJavaEditor.exe")]
    [InlineData(@"C:\java\editor.exe")]
    public void 普通のアプリは同じexeなら同じアプリ(string path) => Assert.False(SharedHost.Is(path));
}
