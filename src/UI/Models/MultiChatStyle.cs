namespace RocketRPG.Models;

public static class MultiChatStyle
{
    public static readonly string[] Names = ["흰색", "빨간색", "연노랑", "연하늘", "연분홍"];
    public static readonly uint[] Colors = [0xFFFFFF, 0xFF3838, 0xFFF2A6, 0xB8E5FF, 0xFFD0E7];
    public static readonly uint[] PingColors = [0xFFD34D, 0x65BBFF, 0xFF7373, 0x7DE09B, 0xC29AFF, 0xFFAB66, 0x80E7E4, 0xFF96D5];
    /// <summary>사람마다 다른 색(핑·마커·이름표)의 이름</summary>
    public static readonly string[] PingColorNames = ["노랑", "파랑", "빨강", "초록", "보라", "주황", "청록", "분홍"];
    public static uint ChatColor(int index) => Colors[System.Math.Clamp(index, 0, Colors.Length - 1)];

    /// <summary>채팅 창·채팅 기록 창이 함께 쓰는 글자색 쿨타임</summary>
    public static readonly ChatColorCooldown ColorCooldown = new();
}

/// <summary>
/// 채팅 글자색 바꾸기 쿨타임: 흰색이 아닌 색으로 바꾸면 30초 동안은 다른 색(흰색 말고)으로 바꿀 수 없습니다.
/// 흰색(0)으로는 언제든 바꿀 수 있습니다. 색을 계속 바꿔 가며 눈에 띄려는 것을 막습니다.
/// </summary>
public sealed class ChatColorCooldown
{
    public const long ChangeMs = 30_000;
    long _readyAt;

    /// <summary>흰색이 아닌 색으로 바꿀 수 있을 때까지 남은 초</summary>
    public int WaitSeconds(long now) => (int)System.Math.Ceiling(System.Math.Max(0, _readyAt - now) / 1000.0);

    /// <summary>from → to로 바꿔도 되는지. 흰색이 아닌 색으로 바꾸면 쿨타임이 시작됩니다.</summary>
    public bool TryChange(int from, int to, long now)
    {
        if (to == from || to == 0) return true;
        if (WaitSeconds(now) > 0) return false;
        _readyAt = now + ChangeMs;
        return true;
    }
}
