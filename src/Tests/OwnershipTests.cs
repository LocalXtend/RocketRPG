using System;
using System.IO;
using System.Text.Json.Nodes;
using RocketRPG.Models;

namespace RocketRPG.Tests;

// 1.1.0 멀티: 스팀 게임을 참가자도 가지고 있는지 확인
public partial class Program
{
    private static void TestGameOwnership()
    {
        Console.WriteLine("--- Testing GameOwnership (steam game check) ---");
        string root = Path.Combine(Path.GetTempPath(), "rr_own_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            // 방장 라이브러리와 참가자 라이브러리 (같은 게임), 다른 게임을 가진 라이브러리
            string MakeGame(string lib, string data)
            {
                string dir = Path.Combine(root, lib, "steamapps", "common", "Hero Story", "game");
                Directory.CreateDirectory(Path.Combine(dir, "www", "data"));
                File.WriteAllText(Path.Combine(dir, "Game.exe"), "nwjs runtime (same for every MV game)");
                File.WriteAllText(Path.Combine(dir, "www", "data", "System.json"), data);
                return dir;
            }
            string hostDir = MakeGame("hostlib", "{\"gameTitle\":\"Hero\"}");
            MakeGame("guestlib", "{\"gameTitle\":\"Hero\"}");
            MakeGame("otherlib", "{\"gameTitle\":\"Zero\"}");   // 같은 크기, 다른 내용

            Assert("Steam location is found from the game folder", GameOwnership.SteamLocation(hostDir) is ("Hero Story", "game"));
            Assert("Folders outside steamapps/common are not steam games", GameOwnership.SteamLocation(Path.Combine(root, "hostlib")) == null);
            Assert("MV data file is System.json", GameOwnership.DataFile(hostDir, 6) == "www/data/System.json");

            var id = GameOwnership.Identify(hostDir, 6, "Game.exe");
            Assert("Host identifies the steam game", id != null && id.ExeRel == "Game.exe" && id.DataRel == "www/data/System.json");
            var want = id!.Describe();
            Assert("Description sent to guests carries no hash", !want.ToJsonString().Contains(Convert.ToHexString(id.Key)));

            string? guestCopy = GameOwnership.FindLocalCopy(want, new[] { Path.Combine(root, "guestlib") });
            Assert("Guest finds the same game in its steam library", guestCopy != null);
            var gate = new OwnershipGate();
            gate.SetGame(id);
            string nonce = gate.NonceFor("g1")!;
            Assert("Each guest is asked once per game", gate.NonceFor("g1") == null);
            Assert("Unchecked guest gets no stream", !gate.Allowed("g1") && gate.Status("g1") == "게임 확인 중");
            Assert("Owner's proof is accepted", gate.Check("g1", gate.Gen, GameOwnership.Proof(GameOwnership.KeyOf(guestCopy!, want), nonce)) == true && gate.Allowed("g1"));

            // 다른 게임(같은 크기, 내용 다름)은 증명이 틀림
            string other = GameOwnership.FindLocalCopy(want, new[] { Path.Combine(root, "otherlib") })!;
            string n2 = gate.NonceFor("g2")!;
            Assert("Different game files are rejected", gate.Check("g2", gate.Gen, GameOwnership.Proof(GameOwnership.KeyOf(other, want), n2)) == false && !gate.Allowed("g2"));
            Assert("Rejected guest is shown as not owning the game", gate.Status("g2") == "게임 없음");
            string n3 = gate.NonceFor("g3")!;
            Assert("Proof for another nonce is rejected", gate.Check("g3", gate.Gen, GameOwnership.Proof(id.Key, nonce)) == false);
            Assert("Missing game answer is rejected", gate.Check("g4", gate.Gen, null) == null && gate.NonceFor("g4") != null && gate.Check("g4", gate.Gen, null) == false);
            Assert("Answers for an old game are ignored", gate.Check("g5", gate.Gen - 1, "x") == null);
            Assert("No library copy: guest cannot find the game", GameOwnership.FindLocalCopy(want, new[] { Path.Combine(root, "nolib") }) == null);

            var bad = (JsonObject)want.DeepClone();
            bad["sub"] = "../../..";
            Assert("Paths outside the steam library are refused", GameOwnership.FindLocalCopy(bad, new[] { Path.Combine(root, "guestlib") }) == null);

            gate.SetGame(null, pending: true);
            Assert("While identifying a new game nobody gets the stream", gate.Required && !gate.Allowed("g1"));
            gate.SetGame(null);
            Assert("Non-steam games are not checked", !gate.Required && gate.Allowed("anyone"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
