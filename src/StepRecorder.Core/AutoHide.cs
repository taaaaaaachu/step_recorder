namespace StepRecorder.Core;

/// <summary>記録バーを引っ込めるか出すか。now は記録バーを出してからの時間。</summary>
public sealed class AutoHide
{
    /// <summary>乗せてから出すまで。通りすがりでは出さない。</summary>
    public static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(150);
    /// <summary>離れてから引っ込めるまで。少しはみ出しただけでは引っ込めない。</summary>
    public static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(800);
    /// <summary>最初と、タブを押して出したときに、マウスが無くても出しておく時間。</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(2.5);

    TimeSpan closeAt = Hold;
    TimeSpan? overSince;

    public bool IsOpen { get; private set; } = true;

    /// <summary>タブが押された。マウスが乗っていなくても(タッチ・読み上げ)しばらく出す。</summary>
    public void Open(TimeSpan now)
    {
        IsOpen = true;
        closeAt = now + Hold;
    }

    /// <summary>over はマウスが上にあるか、pressed はマウスのボタンを押しているか(何かをドラッグして通るだけなら出さない)。</summary>
    public void Update(bool over, bool pressed, TimeSpan now)
    {
        if (IsOpen)
        {
            if (over)
            {
                if (now + CloseDelay > closeAt) closeAt = now + CloseDelay;
            }
            else if (now >= closeAt)
            {
                IsOpen = false;
                overSince = null;
            }
            return;
        }
        if (!over || pressed)
        {
            overSince = null;
            return;
        }
        overSince ??= now;
        if (now - overSince < OpenDelay) return;
        IsOpen = true;
        closeAt = now + CloseDelay;
    }
}
