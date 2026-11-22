namespace StepRecorder.Core.Tests;

public class AutoHideTests
{
    static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    static AutoHide Closed()
    {
        var hide = new AutoHide();
        hide.Update(over: false, pressed: false, Ms(2500));
        Assert.False(hide.IsOpen);
        return hide;
    }

    [Fact]
    public void 最初は少し見せてから引っ込む()
    {
        var hide = new AutoHide();
        hide.Update(false, false, Ms(2400));
        Assert.True(hide.IsOpen);
        hide.Update(false, false, Ms(2500));
        Assert.False(hide.IsOpen);
    }

    [Fact]
    public void 乗せ続けると出て_通りすがりでは出ない()
    {
        var hide = Closed();
        hide.Update(true, false, Ms(3000));
        hide.Update(false, false, Ms(3100)); // 通り過ぎた
        hide.Update(true, false, Ms(3200));
        Assert.False(hide.IsOpen);
        hide.Update(true, false, Ms(3349));
        Assert.False(hide.IsOpen);
        hide.Update(true, false, Ms(3350));
        Assert.True(hide.IsOpen);
    }

    [Fact]
    public void ドラッグで通っても出ない()
    {
        var hide = Closed();
        hide.Update(true, true, Ms(3000));
        hide.Update(true, true, Ms(5000));
        Assert.False(hide.IsOpen);
        hide.Update(true, false, Ms(5100)); // 離してから数え直す
        Assert.False(hide.IsOpen);
        hide.Update(true, false, Ms(5250));
        Assert.True(hide.IsOpen);
    }

    [Fact]
    public void 乗っている間は出たままで_離れて少しすると引っ込む()
    {
        var hide = new AutoHide();
        hide.Update(true, false, Ms(9000));
        hide.Update(true, true, Ms(9500)); // バーのボタンを押していても出たまま
        hide.Update(false, false, Ms(10299));
        Assert.True(hide.IsOpen);
        hide.Update(false, false, Ms(10300));
        Assert.False(hide.IsOpen);
    }

    [Fact]
    public void 少しはみ出して戻れば引っ込まない()
    {
        var hide = new AutoHide();
        hide.Update(true, false, Ms(5000));
        hide.Update(false, false, Ms(5500));
        hide.Update(true, false, Ms(5700));
        hide.Update(false, false, Ms(6400));
        Assert.True(hide.IsOpen);
    }

    [Fact]
    public void タブを押すとマウスが無くてもしばらく出る()
    {
        var hide = Closed();
        hide.Open(Ms(4000));
        hide.Update(false, false, Ms(6400));
        Assert.True(hide.IsOpen);
        hide.Update(false, false, Ms(6500));
        Assert.False(hide.IsOpen);
    }
}
