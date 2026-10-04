namespace RocketRPG.Models;

public static class MultiChatStyle
{
    public static readonly string[] Names = ["흰색", "빨간색", "연노랑", "연하늘", "연분홍"];
    public static readonly uint[] Colors = [0xFFFFFF, 0xFF3838, 0xFFF2A6, 0xB8E5FF, 0xFFD0E7];
    public static readonly uint[] PingColors = [0xFFD34D, 0x65BBFF, 0xFF7373, 0x7DE09B, 0xC29AFF, 0xFFAB66, 0x80E7E4, 0xFF96D5];
    public static uint ChatColor(int index) => Colors[System.Math.Clamp(index, 0, Colors.Length - 1)];
}
