using System;
using System.IO;
using System.Linq;
using RocketRPG.Models;

namespace RocketRPG.Tests;

// 1.0.0 2000/2003 RTP: 사용자 지정 폴더가 먼저, Player에는 설치 기록(레지스트리) 경로를 다시 넘기지 않음
public partial class Program
{
    private static void TestRtpResolver()
    {
        Console.WriteLine("--- Testing RtpResolver (user RTP folders) ---");
        string tmp = Path.Combine(Path.GetTempPath(), "rr_rtp_test_" + Guid.NewGuid().ToString("N")[..6], "한글 RTP");
        string rtp2k = Path.Combine(tmp, "2000");
        string notRtp = Path.Combine(tmp, "empty");
        string rgss = Path.Combine(tmp, "vxace");
        Directory.CreateDirectory(Path.Combine(rtp2k, "CharSet"));
        Directory.CreateDirectory(notRtp);
        Directory.CreateDirectory(Path.Combine(rgss, "Graphics"));
        var saved = RtpResolver.UserPaths;
        try
        {
            Assert("2000 RTP folder is recognized", RtpResolver.LooksLike2kRtp(rtp2k));
            Assert("Empty folder is not an RTP", !RtpResolver.LooksLike2kRtp(notRtp) && !RtpResolver.LooksLikeRgssRtp(notRtp));
            Assert("VX Ace RTP folder is recognized", RtpResolver.LooksLikeRgssRtp(rgss));

            RtpResolver.UserPaths = new(StringComparer.OrdinalIgnoreCase) { ["2000"] = rtp2k, ["vxace"] = rgss };
            var sources = RtpResolver.Resolve2kSources(false);
            Assert("User 2000 RTP folder comes first", sources.Count > 0 && sources[0] == (rtp2k, "user"), string.Join("; ", sources));
            string player = RtpResolver.PlayerRtpPaths(false);
            Assert("User folder (Korean path) is passed to the Player", player.Split(';').Contains(rtp2k), player);
            Assert("Registry folders are not passed to the Player again",
                sources.Where(s => s.Source == "registry").All(s => !player.Split(';').Contains(s.Path)), player);
            Assert("2003 does not use the 2000 folder", !RtpResolver.Resolve2kPaths(true).Contains(rtp2k));

            string game = Path.Combine(tmp, "game");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "Game.ini"), "[Game]\r\nRTP=RPGVXAce\r\n");
            Assert("User VX Ace RTP folder is used", RtpResolver.ResolveRgss(game, CoreInterop.EngineAce).FirstOrDefault() == Path.GetFullPath(rgss));
            Assert("With a user RTP the game is not reported as missing RTP", RtpResolver.MissingRgssRtp(game, CoreInterop.EngineAce) == null);
            RtpResolver.UserPaths = new(StringComparer.OrdinalIgnoreCase) { ["2000"] = Path.Combine(tmp, "gone") };
            Assert("A user folder that no longer exists is ignored", !RtpResolver.Resolve2kPaths(false).Contains(Path.Combine(tmp, "gone")));
        }
        finally
        {
            RtpResolver.UserPaths = saved;
            try { Directory.Delete(Path.GetDirectoryName(tmp)!, true); } catch { }
        }
    }
}
