#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RocketRPG.Models;

namespace RocketRPG.Views;

// ── 멀티 노트 공유 ──
// 방장의 게임 노트(모든 페이지)를 참가자에게 보여 주고, 기본은 모두 고칠 수 있습니다(방장이 잠글 수 있음).
// 참가자는 메모·글상자·이미지(스크린샷)를 넣고, 고치고, 지울 수 있고, 페이지 관리는 방장만 합니다.
// 방장 노트가 기준입니다: 바뀌면 잠시 뒤 항목 단위로 차이를 찾아 보내고, 참가자가 고친 것도 방장에게 보내 적용한 뒤 모두에게 다시 보냅니다.
// 같은 항목을 동시에 고치면 나중 것이 남고, 지금 글을 쓰거나 끌고 있는 항목은 들어오는 변경으로 덮어쓰지 않습니다.
// 전달은 WebRTC 데이터 채널 "notes" (서버를 거치지 않음, 큰 것은 방송 페이지가 나눠 보냄).
public partial class MainWindow
{
    const long MaxNoteImageBytes = 16 << 20;

    // 방장
    NotesStore? _shareStore;                                // 공유 중인 노트 (방장의 게임 노트)
    Dictionary<string, string> _shareState = new();         // 항목 id → "페이지 id\nJSON" (참가자에게 마지막으로 보낸 모양)
    string _sharePages = "";
    readonly HashSet<string> _shareImages = new(StringComparer.OrdinalIgnoreCase);
    // 참가자
    NotesStore? _roomNotes;                                 // 방 노트 (방장 노트의 사본)
    Dictionary<string, string> _roomState = new();          // 방장과 맞춰 둔 모양
    readonly HashSet<string> _roomImagesKnown = new(StringComparer.OrdinalIgnoreCase);
    bool _roomEditable = true, _applyingRemote;
    readonly DispatcherTimer _notesTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    bool _notesTimerHooked;

    static readonly JsonSerializerOptions NoteJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>방 상태가 바뀔 때마다 (SyncMultiMediaOnce에서): 공유할 노트, 방 노트, 잠금을 맞춥니다.</summary>
    void SyncMultiNotes(string role)
    {
        if (!_notesTimerHooked)
        {
            _notesTimerHooked = true;
            _notesTimer.Tick += (_, _) => { _notesTimer.Stop(); if (_shareStore != null) HostNotesDiff(); else if (_roomNotes != null) GuestNotesDiff(); };
        }

        var share = role == "host" ? NotesHost.GameStore : null;
        if (!ReferenceEquals(share, _shareStore))
        {
            if (_shareStore != null) _shareStore.Dirty -= OnNotesDirty;
            _shareStore = share;
            _shareState = new();
            _sharePages = "";
            _shareImages.Clear();
            if (share != null) share.Dirty += OnNotesDirty;
            if (role == "host") SendNotesFull("*");
        }

        if (role == "guest" && _roomNotes == null)
        {
            _roomNotes = NotesStore.ForRoom("방장이 게임을 켜면 방 노트가 보입니다");
            _roomNotes.Dirty += OnNotesDirty;
            _roomState = new();
            _roomImagesKnown.Clear();
            NotesHost.OpenRoom(_roomNotes);
        }
        else if (role != "guest" && _roomNotes != null)
        {
            _roomNotes.Dirty -= OnNotesDirty;
            NotesHost.CloseRoom();
            try { if (Directory.Exists(_roomNotes.Folder)) Directory.Delete(_roomNotes.Folder, true); } catch { }
            _roomNotes = null;
        }
        if (role == "guest" && _multi != null)
        {
            bool editable = _multi.Room.Settings.NotesEditable;
            if (editable != _roomEditable) ShowHudMessage(editable ? "방장이 노트 고치기를 열었습니다." : "방장이 노트를 잠갔습니다 (보기만 됨).", 3000);
            _roomEditable = editable;
            NotesHost.SetRoomReadOnly(!editable);
        }
    }

    void OnNotesDirty()
    {
        if (_applyingRemote) return;
        _notesTimer.Stop();
        _notesTimer.Start();
    }

    // ── 모양 비교 ──

    static (string pages, Dictionary<string, string> items) NotesSnapshot(NotesStore store)
    {
        var items = new Dictionary<string, string>();
        foreach (var p in store.Doc.Pages)
            foreach (var i in p.Items)
                items[i.Id] = p.Id + "\n" + JsonSerializer.Serialize(i, NoteJson);
        return (string.Join("\u0001", store.Doc.Pages.Select(p => p.Id + "\t" + p.Title)), items);
    }

    static JsonArray PagesJson(NotesStore store) =>
        new(store.Doc.Pages.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["title"] = p.Title }).ToArray());

    static JsonObject ItemJson(string state)
    {
        int nl = state.IndexOf('\n');
        return new JsonObject { ["page"] = state[..nl], ["item"] = JsonNode.Parse(state[(nl + 1)..]) };
    }

    // ── 보내기 (방송 페이지를 거쳐 데이터 채널로) ──

    void NotesSend(string to, JsonObject msg) =>
        PostMulti(new JsonObject { ["t"] = "nsend", ["to"] = to, ["data"] = msg.ToJsonString(NoteJson) }.ToJsonString());

    void SendNoteImage(string to, NotesStore store, string name)
    {
        try
        {
            string path = store.ImagePath(name);
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > MaxNoteImageBytes) return;
            NotesSend(to, new JsonObject { ["op"] = "img", ["name"] = name, ["data"] = Convert.ToBase64String(File.ReadAllBytes(path)) });
        }
        catch (Exception ex) { UiLog.Write($"multi notes: image send failed {ex.Message}"); }
    }

    static IEnumerable<string> ImagesOf(IEnumerable<string> states) =>
        states.Select(s => JsonNode.Parse(s[(s.IndexOf('\n') + 1)..]))
              .Where(n => n?["kind"]?.GetValue<string>() == "image")
              .Select(n => n?["image"]?.GetValue<string>() ?? "")
              .Where(n => n.Length > 0);

    // ── 방장 ──

    /// <summary>노트 전체 (새 참가자, 게임이 바뀜). 이미지를 먼저 보냅니다.</summary>
    void SendNotesFull(string to)
    {
        if (_shareStore == null) { NotesSend(to, new JsonObject { ["op"] = "none" }); return; }
        var (pages, items) = NotesSnapshot(_shareStore);
        foreach (var name in ImagesOf(items.Values).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SendNoteImage(to, _shareStore, name);
            if (to == "*") _shareImages.Add(name);
        }
        NotesSend(to, new JsonObject
        {
            ["op"] = "full",
            ["title"] = _shareStore.Doc.GameTitle,
            ["pages"] = PagesJson(_shareStore),
            ["items"] = new JsonArray(items.Values.Select(v => (JsonNode)ItemJson(v)).ToArray()),
        });
        if (to == "*") { _shareState = items; _sharePages = pages; }
    }

    void HostNotesDiff()
    {
        if (_shareStore == null || _multi is not { InRoom: true, IsHost: true }) return;
        var (pages, items) = NotesSnapshot(_shareStore);
        var upserts = items.Where(kv => !_shareState.TryGetValue(kv.Key, out var old) || old != kv.Value).Select(kv => kv.Value).ToList();
        var deletes = _shareState.Keys.Where(id => !items.ContainsKey(id)).ToList();
        bool pagesChanged = pages != _sharePages;
        if (upserts.Count == 0 && deletes.Count == 0 && !pagesChanged) return;
        foreach (var name in ImagesOf(upserts).Where(n => _shareImages.Add(n)).ToList()) SendNoteImage("*", _shareStore, name);
        var msg = new JsonObject
        {
            ["op"] = "diff",
            ["upserts"] = new JsonArray(upserts.Select(v => (JsonNode)ItemJson(v)).ToArray()),
            ["deletes"] = new JsonArray(deletes.Select(d => (JsonNode)d).ToArray()),
        };
        if (pagesChanged) msg["pages"] = PagesJson(_shareStore);
        NotesSend("*", msg);
        _shareState = items;
        _sharePages = pages;
    }

    // ── 받기 ──

    /// <summary>방송 페이지가 데이터 채널에서 받은 노트 메시지 (방장: from = 참가자 id)</summary>
    void OnNotesMessage(string from, string data)
    {
        JsonObject? m;
        try { m = JsonNode.Parse(data) as JsonObject; } catch { return; }
        if (m == null) return;
        if (OnMultiVoteMessage(from, m)) return;
        if (OnMultiToolMessage(from, m)) return;
        if (IsMultiGuest && from != "host") return;
        string op = m["op"]?.GetValue<string>() ?? "";
        if (_shareStore != null && _multi?.IsHost == true)
        {
            // 방장: 참가자가 고친 것 (잠겨 있으면 받지 않음)
            if (!_multi.Room.Settings.NotesEditable) return;
            if (op == "img") SaveNoteImage(_shareStore, m);
            else if (op == "edit")
            {
                var ids = ApplyNoteChanges(_shareStore, m, allowNewPages: false);
                if (ids.Count > 0)
                {
                    NotesHost.RefreshFromSync(_shareStore, ids, pagesChanged: false);
                    _shareStore.MarkDirty();   // 저장 + 모두에게 다시 보냄
                }
            }
            return;
        }
        if (_roomNotes == null) return;
        switch (op)
        {
            case "dlg":
            case "dlgs":
                OnDialogueMessage(m);
                break;
            case "img":
            {
                string? name = SaveNoteImage(_roomNotes, m);
                if (name == null) break;
                _roomImagesKnown.Add(name);
                var ids = _roomNotes.Doc.Pages.SelectMany(p => p.Items).Where(i => i.Kind == "image" && string.Equals(i.Image, name, StringComparison.OrdinalIgnoreCase)).Select(i => i.Id).ToList();
                if (ids.Count > 0) NotesHost.RefreshFromSync(_roomNotes, ids, pagesChanged: false, force: true);
                break;
            }
            case "none":
                ApplyRoomFull(title: "방장이 게임을 켜면 방 노트가 보입니다", pages: null, items: null);
                break;
            case "full":
                ApplyRoomFull(m["title"]?.GetValue<string>() ?? "", m["pages"] as JsonArray, m["items"] as JsonArray);
                break;
            case "diff":
            {
                _applyingRemote = true;
                try
                {
                    bool pagesChanged = m["pages"] is JsonArray pa && ReconcilePages(_roomNotes, pa);
                    var ids = ApplyNoteChanges(_roomNotes, m, allowNewPages: false, track: _roomState);
                    NotesHost.RefreshFromSync(_roomNotes, ids, pagesChanged);
                }
                finally { _applyingRemote = false; }
                break;
            }
        }
    }

    /// <summary>참가자: 방 노트를 방장 노트로 통째로 바꿈 (지금 다루는 항목은 그대로)</summary>
    void ApplyRoomFull(string title, JsonArray? pages, JsonArray? items)
    {
        if (_roomNotes == null) return;
        _applyingRemote = true;
        try
        {
            var doc = _roomNotes.Doc;
            doc.GameTitle = title;
            ReconcilePages(_roomNotes, pages ?? new JsonArray(new JsonObject { ["id"] = "none", ["title"] = "노트" }));
            var oldIds = doc.Pages.SelectMany(p => p.Items).Select(i => i.Id).ToList();
            var busy = doc.Pages.SelectMany(p => p.Items).Where(i => NotesHost.IsItemBusy(_roomNotes, i.Id)).ToDictionary(i => i.Id);
            foreach (var p in doc.Pages) p.Items.RemoveAll(i => !busy.ContainsKey(i.Id));
            _roomState = new();
            foreach (var entry in items ?? new JsonArray())
            {
                string pageId = entry?["page"]?.GetValue<string>() ?? "";
                var item = entry?["item"]?.Deserialize<NoteItem>(NoteJson);
                var page = doc.Pages.FirstOrDefault(p => p.Id == pageId);
                if (item == null || page == null) continue;
                _roomState[item.Id] = pageId + "\n" + JsonSerializer.Serialize(item, NoteJson);
                if (item.Kind == "image") _roomImagesKnown.Add(item.Image);
                if (!busy.ContainsKey(item.Id)) page.Items.Add(item);
            }
            // 없어진 항목의 그림도 지우도록 예전 id까지 함께
            NotesHost.RefreshFromSync(_roomNotes, oldIds.Concat(doc.Pages.SelectMany(p => p.Items).Select(i => i.Id)), pagesChanged: true, force: true);
        }
        finally { _applyingRemote = false; }
    }

    /// <summary>페이지 목록을 방장 것에 맞춤 (같은 id의 페이지는 보던 위치·배율을 유지). 바뀌었으면 true.</summary>
    static bool ReconcilePages(NotesStore store, JsonArray pages)
    {
        var doc = store.Doc;
        string before = string.Join("\u0001", doc.Pages.Select(p => p.Id + "\t" + p.Title));
        string activeId = doc.Pages.Count > 0 ? store.ActivePage.Id : "";
        var old = doc.Pages.ToDictionary(p => p.Id);
        var list = new List<NotePage>();
        foreach (var n in pages)
        {
            string id = n?["id"]?.GetValue<string>() ?? "";
            if (id.Length == 0) continue;
            var p = old.TryGetValue(id, out var existing) ? existing : new NotePage { Id = id };
            p.Title = n?["title"]?.GetValue<string>() ?? p.Title;
            list.Add(p);
        }
        if (list.Count == 0) list.Add(new NotePage { Title = "노트" });
        doc.Pages = list;
        int active = list.FindIndex(p => p.Id == activeId);
        doc.ActivePage = active >= 0 ? active : 0;
        return before != string.Join("\u0001", list.Select(p => p.Id + "\t" + p.Title));
    }

    /// <summary>
    /// 항목 추가·수정(upserts)과 삭제(deletes)를 노트에 적용. 지금 다루는 항목은 건너뜁니다. 적용한 항목 id를 돌려줌.
    /// track이 있으면 적용한 모양을 거기에 기록합니다 (참가자: 방장과 맞춘 모양).
    /// </summary>
    List<string> ApplyNoteChanges(NotesStore store, JsonObject m, bool allowNewPages, Dictionary<string, string>? track = null)
    {
        var changed = new List<string>();
        var doc = store.Doc;
        foreach (var entry in m["upserts"] as JsonArray ?? new JsonArray())
        {
            string pageId = entry?["page"]?.GetValue<string>() ?? "";
            NoteItem? item;
            try { item = entry?["item"]?.Deserialize<NoteItem>(NoteJson); } catch { continue; }
            var page = doc.Pages.FirstOrDefault(p => p.Id == pageId);
            if (item == null || item.Id.Length == 0 || page == null || NotesHost.IsItemBusy(store, item.Id)) continue;
            if (item.Kind == "image") item.Image = Path.GetFileName(item.Image);   // 다른 폴더를 가리키지 못하게
            foreach (var p in doc.Pages)
            {
                int at = p.Items.FindIndex(i => i.Id == item.Id);
                if (at < 0) continue;
                if (ReferenceEquals(p, page)) { p.Items[at] = item; page = null; }
                else p.Items.RemoveAt(at);   // 다른 페이지로 옮겨짐
                break;
            }
            page?.Items.Add(item);
            if (track != null) track[item.Id] = pageId + "\n" + JsonSerializer.Serialize(item, NoteJson);
            changed.Add(item.Id);
        }
        foreach (var node in m["deletes"] as JsonArray ?? new JsonArray())
        {
            string id = node?.GetValue<string>() ?? "";
            if (id.Length == 0 || NotesHost.IsItemBusy(store, id)) continue;
            foreach (var p in doc.Pages) if (p.Items.RemoveAll(i => i.Id == id) > 0) changed.Add(id);
            track?.Remove(id);
        }
        return changed;
    }

    /// <summary>받은 이미지를 노트 폴더에 저장. 파일 이름 (못 하면 null)</summary>
    static string? SaveNoteImage(NotesStore store, JsonObject m)
    {
        try
        {
            string name = Path.GetFileName(m["name"]?.GetValue<string>() ?? "");
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (name.Length == 0 || ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")) return null;
            byte[] bytes = Convert.FromBase64String(m["data"]?.GetValue<string>() ?? "");
            if (bytes.Length == 0 || bytes.Length > MaxNoteImageBytes) return null;
            Directory.CreateDirectory(store.ImageFolder);
            string path = store.ImagePath(name);
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            return name;
        }
        catch (Exception ex) { UiLog.Write($"multi notes: image save failed {ex.Message}"); return null; }
    }

    // ── 참가자 ──

    /// <summary>참가자가 고친 것을 방장에게 (새 이미지는 먼저)</summary>
    void GuestNotesDiff()
    {
        if (_roomNotes == null || !_roomEditable || !IsMultiGuest) return;
        var (_, items) = NotesSnapshot(_roomNotes);
        var upserts = items.Where(kv => !_roomState.TryGetValue(kv.Key, out var old) || old != kv.Value).Select(kv => kv.Value).ToList();
        var deletes = _roomState.Keys.Where(id => !items.ContainsKey(id)).ToList();
        if (upserts.Count == 0 && deletes.Count == 0) return;
        foreach (var name in ImagesOf(upserts).Where(n => _roomImagesKnown.Add(n)).ToList()) SendNoteImage("host", _roomNotes, name);
        NotesSend("host", new JsonObject
        {
            ["op"] = "edit",
            ["upserts"] = new JsonArray(upserts.Select(v => (JsonNode)ItemJson(v)).ToArray()),
            ["deletes"] = new JsonArray(deletes.Select(d => (JsonNode)d).ToArray()),
        });
        _roomState = items;
    }

    // ── 참가자 스크린샷 (방장 영상에서) ──

    TaskCompletionSource<byte[]?>? _snapWaiter;

    Task<byte[]?> CaptureGuestFrameAsync()
    {
        _snapWaiter?.TrySetResult(null);
        _snapWaiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = _snapWaiter;
        PostMulti("{\"t\":\"snap\"}");
        _ = Task.Delay(3000).ContinueWith(_ => waiter.TrySetResult(null));
        return waiter.Task;
    }

    void OnMultiPageSnap(JsonObject m)
    {
        byte[]? png = null;
        try { if (m["png"]?.GetValue<string>() is { Length: > 0 } b64) png = Convert.FromBase64String(b64); } catch { }
        _snapWaiter?.TrySetResult(png);
    }

    /// <summary>참가자 스크린샷: 방장 화면(받은 영상)을 찍어 보관함에, 노트로 찍으면 방 노트에도 넣음</summary>
    async Task TakeGuestScreenshot(bool toNote)
    {
        byte[]? png = await CaptureGuestFrameAsync();
        if (png == null) { ShowHudMessage("스크린샷을 찍지 못했습니다 (방장 화면을 받는 중이 아님)."); return; }
        string title = string.IsNullOrEmpty(_multi?.Room.Game) ? "멀티" : _multi!.Room.Game;
        string saved;
        try { saved = await Task.Run(() => ScreenshotStore.Save(title, png)); }
        catch (Exception ex) { UiLog.Write($"screenshot: save failed {ex.Message}"); ShowHudMessage("스크린샷을 저장하지 못했습니다."); return; }
        if (toNote && _roomNotes != null)
        {
            if (!_roomEditable) { ShowHudMessage($"스크린샷: {Path.GetFileName(saved)} (방 노트가 잠겨 있어 노트에는 넣지 않았습니다)"); return; }
            NotesHost.ShowRoomNotes();
            NotesHost.AddScreenshot(png);
            ShowHudMessage($"스크린샷: {Path.GetFileName(saved)} (방 노트에도 추가)");
        }
        else ShowHudMessage($"스크린샷 저장: {Path.GetFileName(saved)}");
    }

    void OnMultiNotesEditable(object sender, RoutedEventArgs e)
    {
        if (_multi is not { InRoom: true, IsHost: true }) return;
        bool on = MultiNotesEditItem.IsChecked;
        _multi.Send(new { t = "settings", notesEditable = on });
        ShowHudMessage(on ? "노트: 참가자도 고칠 수 있음" : "노트: 참가자는 보기만 (잠금)");
    }
}
