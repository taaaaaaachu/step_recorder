using System.Globalization;

namespace StepRecorder.Core;

/// <summary>画面に出す動画の時刻。1 時間からは時も出す(24 時間を超えても一周しない)。端数は切り捨て。</summary>
public static class TimeText
{
    /// <summary>0:05.0、1:00:05.0</summary>
    public static string Position(TimeSpan t) => Seconds(t) + t.ToString(@"\.f", CultureInfo.InvariantCulture);

    /// <summary>0:05、1:00:05</summary>
    public static string Seconds(TimeSpan t) => t.TotalHours >= 1
        ? (int)t.TotalHours + t.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
        : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
