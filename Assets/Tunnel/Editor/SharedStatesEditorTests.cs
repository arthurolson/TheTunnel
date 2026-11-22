using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Checks saved states shared between headsets (see <see cref="TunnelSharedStates"/>) against a fake GitHub kept in
/// memory, so nothing goes to the real repository and no real token is used: a state from an earlier version gets an
/// id; offline, and with a refused token, a save stays on the headset marked not uploaded; with the token it uploads
/// (its .json and .txt, private bits of web addresses taken out) and moves to the shared list; a second headset with no
/// token reads the list, downloads the states, restores one offline, can't delete a shared one or upload its own, while
/// the first headset deletes one from every headset; an upload cut short before is finished; the token never reaches
/// anything but the API, nor the log. Each "headset" is a folder of the test's own, removed afterwards. Runs in Edit
/// mode from Tunnel > Menu > Test Shared Saved States and logs PASS or FAIL with the tag [SaveState].
/// Tunnel > Menu > Read Shared Saved States (real repository) reads the real list, without a token, into a folder of
/// its own.
/// </summary>
static class SharedStatesEditorTests
{
    // Clearly not a real token: the fake GitHub only accepts this.
    const string k_FakeToken = "fake-token-for-the-editor-test-0000";

    public static string lastReport { get; private set; } = "";

    /// <summary>GitHub's Contents API and raw files for one repository, in memory.</summary>
    class FakeGitHub
    {
        public readonly Dictionary<string, string> files = new();
        readonly Dictionary<string, string> m_Shas = new();
        public bool offline;
        public bool refuseToken;
        public bool tokenOffApi;
        public readonly List<string> requests = new();
        public readonly List<string> bodies = new();
        int m_Next;
        readonly string m_Api, m_Raw;

        [Serializable]
        class Body
        {
            public string message;
            public string content;
            public string branch;
            public string sha;
        }

        public FakeGitHub()
        {
            var (owner, name, branch) = TunnelSharedStates.repository;
            m_Api = $"https://api.github.com/repos/{owner}/{name}/contents/";
            m_Raw = $"https://raw.githubusercontent.com/{owner}/{name}/{branch}/";
        }

        public string Sha(string path) => m_Shas.TryGetValue(path, out var sha) ? sha : null;

        public void Add(string path, string text)
        {
            files[path] = text;
            m_Shas[path] = $"sha{++m_Next:D4}";
        }

        public Task<TunnelSharedStates.Reply> Send(string method, string url, string body, string token) =>
            Task.FromResult(Handle(method, url, body, token));

        static TunnelSharedStates.Reply Reply(long code, string text = "") => new() { code = code, text = text };

        string Describe(string path) =>
            $"{{\"name\":\"{Path.GetFileName(path)}\",\"path\":\"{path}\",\"sha\":\"{m_Shas[path]}\",\"type\":\"file\",\"download_url\":\"{m_Raw}{path}\"}}";

        TunnelSharedStates.Reply Handle(string method, string url, string body, string token)
        {
            if (offline)
                return new TunnelSharedStates.Reply { code = 0, error = "Cannot resolve destination host (fake)" };
            requests.Add($"{method} {url}");
            if (url.StartsWith(m_Raw))
            {
                if (token != null)
                    tokenOffApi = true;
                string rawPath = Uri.UnescapeDataString(url.Substring(m_Raw.Length));
                return files.TryGetValue(rawPath, out var text) ? Reply(200, text) : Reply(404, "404: Not Found");
            }
            if (!url.StartsWith(m_Api))
            {
                if (token != null)
                    tokenOffApi = true;
                return Reply(404);
            }
            string rest = url.Substring(m_Api.Length);
            string path = Uri.UnescapeDataString(rest.Split('?')[0]);
            if (method == "GET")
            {
                if (path == TunnelSharedStates.k_Folder)
                {
                    var inFolder = files.Keys.Where(k => k.StartsWith(TunnelSharedStates.k_Folder + "/")).OrderBy(k => k).ToList();
                    return inFolder.Count == 0 ? Reply(404, "{\"message\":\"Not Found\"}") : Reply(200, "[" + string.Join(",", inFolder.Select(Describe)) + "]");
                }
                return files.ContainsKey(path) ? Reply(200, Describe(path)) : Reply(404, "{\"message\":\"Not Found\"}");
            }
            if (token != k_FakeToken || refuseToken)
                return Reply(401, "{\"message\":\"Bad credentials\"}");
            bodies.Add(body);
            var request = JsonUtility.FromJson<Body>(body);
            if (method == "PUT")
            {
                if (files.ContainsKey(path) && request.sha != m_Shas[path])
                    return Reply(422, "{\"message\":\"Invalid request.\\n\\n\\\"sha\\\" wasn't supplied.\"}");
                Add(path, Encoding.UTF8.GetString(Convert.FromBase64String(request.content)));
                return Reply(201, $"{{\"content\":{Describe(path)}}}");
            }
            if (method == "DELETE")
            {
                if (!files.ContainsKey(path))
                    return Reply(404, "{\"message\":\"Not Found\"}");
                if (request.sha != m_Shas[path])
                    return Reply(409, "{\"message\":\"does not match\"}");
                files.Remove(path);
                m_Shas.Remove(path);
                return Reply(200, "{\"commit\":{}}");
            }
            return Reply(405);
        }
    }

    static TunnelSavedState MakeState(string name, int segments, string url)
    {
        var state = new TunnelSavedState
        {
            name = name,
            saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            savedFrom = "test",
        };
        for (int i = 0; i < segments; i++)
            state.segments.Add(new SegmentState { color = new Vector4(0.1f * i, 0.5f, 0.5f, 1f) });
        state.figures.Add(new Placement { segment = 0, position = new Vector3(0.1f, 1.2f, 0.3f) });
        if (url != null)
            state.panels.Add(new PlacedPanel { url = url, width = 1f, aspect = 1.6f });
        return state;
    }

    [MenuItem("Tunnel/Menu/Test Shared Saved States")]
    public static string Test()
    {
        var report = new StringBuilder("[SaveState] shared saved states test (fake GitHub)\n");
        int failures = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }

        string previousFolder = TunnelStateStore.folder;
        var previousSend = TunnelSharedStates.send;
        string previousToken = TunnelSharedStates.tokenOverride;
        string root = Path.Combine(Application.temporaryCachePath, "SharedStatesTest");
        string headsetA = Path.Combine(root, "A"), headsetB = Path.Combine(root, "B");
        var logged = new StringBuilder();
        void OnLog(string message, string stack, LogType type) => logged.AppendLine(message);
        Application.logMessageReceived += OnLog;
        var github = new FakeGitHub();
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            TunnelSharedStates.send = github.Send;
            void Headset(string folder, string token)
            {
                TunnelStateStore.folder = folder;
                TunnelSharedStates.tokenOverride = token;
                TunnelSharedStates.ResetForTests();
            }
            void Wait(Task task) => Check(task.IsCompleted, "the fake GitHub answered at once");
            string folderName = TunnelSharedStates.k_Folder;

            // Headset A: a state saved by 22.11.2026.1 (a named file) gets an id and keeps its name.
            Headset(headsetA, "");
            Directory.CreateDirectory(headsetA);
            var old = MakeState("State 1", 2, null);
            File.WriteAllText(Path.Combine(headsetA, "State 1.json"), JsonUtility.ToJson(old));
            File.WriteAllText(Path.Combine(headsetA, "State 1.txt"), "21 Nov 10:00 · 2 segments · 1 thing");
            var ids = TunnelStateStore.Ids();
            Check(ids.Count == 1 && TunnelStateStore.IsId(ids[0]) && TunnelStateStore.Name(ids[0]) == "State 1"
                && !File.Exists(Path.Combine(headsetA, "State 1.json")), $"an earlier version's State 1 is now {ids.FirstOrDefault()}, still called State 1");
            string id1 = ids.FirstOrDefault();

            // No token: it stays, marked not uploaded.
            Wait(TunnelSharedStates.Refresh(true));
            var everything = TunnelSharedStates.Everything();
            Check(everything.Count == 1 && everything[0].local && github.files.Count == 0, "with no token it stays on the headset, marked not uploaded");
            Check(TunnelSharedStates.status.Contains("No GitHub token"), $"the status says so: \"{TunnelSharedStates.status}\"");

            // Offline with the token: still kept.
            Headset(headsetA, k_FakeToken);
            github.offline = true;
            Wait(TunnelSharedStates.Refresh(true));
            Check(TunnelStateStore.Ids().Count == 1 && TunnelSharedStates.Everything().Single().local, "offline it stays on the headset");
            Check(TunnelSharedStates.status.StartsWith("Offline"), $"the status says offline: \"{TunnelSharedStates.status}\"");
            github.offline = false;

            // A refused token: still kept.
            github.refuseToken = true;
            Wait(TunnelSharedStates.Refresh(true));
            Check(TunnelStateStore.Ids().Count == 1 && github.files.Count == 0, "with a refused token it stays on the headset");
            Check(TunnelSharedStates.status.Contains("refused"), $"the status says the token was refused: \"{TunnelSharedStates.status}\"");
            github.refuseToken = false;

            // Online with the token: it uploads, and moves to the shared list.
            Wait(TunnelSharedStates.Refresh(true));
            Check(github.files.ContainsKey($"{folderName}/{id1}.json") && github.files.ContainsKey($"{folderName}/{id1}.txt"),
                $"it uploaded {id1}.json and {id1}.txt");
            Check(TunnelStateStore.Ids().Count == 0, "it is no longer only on the headset");
            everything = TunnelSharedStates.Everything();
            Check(everything.Count == 1 && !everything[0].local && everything[0].name == "State 1", "the list shows it as shared");
            Check(github.files.TryGetValue($"{folderName}/{id1}.txt", out var txt) && txt.StartsWith("State 1\n") && txt.Contains("2 segments"),
                $"its .txt: \"{txt?.Replace("\n", " / ")}\"");
            Check(TunnelSharedStates.LoadNow(id1)?.segments.Count == 2, "it still restores on this headset (its copy is kept)");

            // A new save with private bits in a web address.
            var withUrl = MakeState(TunnelStateStore.NextName(TunnelSharedStates.Everything().Select(i => i.name)), 3,
                "https://user:pw@example.org/page?v=12&access_token=SECRET1&sig=SECRET2#top");
            Check(withUrl.name == "State 2", $"the next name is {withUrl.name}");
            TunnelStateStore.Save(withUrl);
            string id2 = withUrl.id;
            Wait(TunnelSharedStates.UploadPending());
            github.files.TryGetValue($"{folderName}/{id2}.json", out var uploaded);
            Check(uploaded != null && uploaded.Contains("example.org/page?v=12") && !uploaded.Contains("SECRET") && !uploaded.Contains("pw@"),
                "it uploaded, with the password, token and signature taken out of the web address");
            Check(github.bodies.All(b => !b.Contains(k_FakeToken)), "no request body holds the token");

            // An upload cut short before (the .json there, the .txt not): finished, replacing it.
            var cut = MakeState("State 3", 1, null);
            TunnelStateStore.Save(cut);
            github.Add($"{folderName}/{cut.id}.json", "{\"partial\":true}");
            Wait(TunnelSharedStates.UploadPending());
            Check(github.files.TryGetValue($"{folderName}/{cut.id}.json", out var replaced) && replaced.Contains("\"State 3\"")
                && github.files.ContainsKey($"{folderName}/{cut.id}.txt") && TunnelStateStore.Ids().Count == 0, "an upload cut short before is finished");

            // Headset B, no token: reads the list and downloads every state (saved in the same second here: in order of their numbers).
            Headset(headsetB, "");
            Wait(TunnelSharedStates.Refresh(true));
            everything = TunnelSharedStates.Everything();
            Check(everything.Select(i => i.name).SequenceEqual(new[] { "State 1", "State 2", "State 3" }) && everything.All(i => !i.local),
                $"another headset lists them: {string.Join(", ", everything.Select(i => $"{i.name} ({i.info})"))}");
            Check(File.Exists(Path.Combine(headsetB, "Shared", id2 + ".json")), "and keeps a copy of each");
            var loaded = TunnelSharedStates.LoadNow(id2);
            Check(loaded != null && loaded.segments.Count == 3 && loaded.panels.Count == 1, "a shared state reads back there");

            // B offline after a restart: the list and the states are still there.
            github.offline = true;
            Headset(headsetB, "");
            Wait(TunnelSharedStates.Refresh(true));
            Check(TunnelSharedStates.Everything().Count == 3, "offline after a restart it still lists them");
            var offlineLoad = TunnelSharedStates.Load(id1);
            Wait(offlineLoad);
            Check(offlineLoad.Result.state?.segments.Count == 2, "and restores one offline");
            github.offline = false;

            // B can't delete a shared state, nor upload its own.
            var refused = TunnelSharedStates.Delete(id1);
            Wait(refused);
            Check(!refused.Result.ok && refused.Result.message.Contains("token") && github.files.ContainsKey($"{folderName}/{id1}.json"),
                $"without a token, Delete says why and keeps it: \"{refused.Result.message}\"");
            var fromB = MakeState(TunnelStateStore.NextName(TunnelSharedStates.Everything().Select(i => i.name)), 1, null);
            TunnelStateStore.Save(fromB);
            Wait(TunnelSharedStates.UploadPending());
            everything = TunnelSharedStates.Everything();
            Check(fromB.name == "State 4" && everything.Count == 4 && everything.Single(i => i.id == fromB.id).local,
                $"its own save ({fromB.name}) stays there, marked not uploaded");
            Check(fromB.id != id1 && fromB.id != id2 && fromB.id != cut.id, "its file name differs from the other headset's");
            var localDelete = TunnelSharedStates.Delete(fromB.id);
            Wait(localDelete);
            Check(localDelete.Result.ok && TunnelSharedStates.Everything().Count == 3, $"one only on this headset deletes here: \"{localDelete.Result.message}\"");

            // A deletes State 2 from every headset.
            Headset(headsetA, k_FakeToken);
            var deleted = TunnelSharedStates.Delete(id2);
            Wait(deleted);
            Check(deleted.Result.ok && !github.files.Keys.Any(k => k.Contains(id2)), $"with the token, Delete removes both files from the repository: \"{deleted.Result.message}\"");
            Headset(headsetB, "");
            Wait(TunnelSharedStates.Refresh(true));
            Check(TunnelSharedStates.Everything().All(i => i.id != id2) && !File.Exists(Path.Combine(headsetB, "Shared", id2 + ".json")),
                "the other headset drops it (and its copy) when it reads the list again");

            Check(!github.tokenOffApi, "the token was only sent to api.github.com");
        }
        catch (Exception e)
        {
            Check(false, $"the test stopped: {e}");
        }
        finally
        {
            Application.logMessageReceived -= OnLog;
            TunnelStateStore.folder = previousFolder;
            TunnelSharedStates.send = previousSend;
            TunnelSharedStates.tokenOverride = previousToken;
            TunnelSharedStates.ResetForTests();
            try { Directory.Delete(root, true); } catch { }
        }
        Check(!logged.ToString().Contains(k_FakeToken), "the token is never logged");
        report.AppendLine($"  ({github.requests.Count} requests to the fake GitHub)");
        report.Insert(0, failures == 0 ? "PASS " : $"FAIL ({failures}) ");
        lastReport = report.ToString();
        if (failures == 0)
            Debug.Log(lastReport);
        else
            Debug.LogError(lastReport);
        return lastReport;
    }

    /// <summary>Reads the real repository's saved states without a token, into a folder of its own; logs what it found.</summary>
    [MenuItem("Tunnel/Menu/Read Shared Saved States (real repository)")]
    public static async void ReadReal()
    {
        string previousFolder = TunnelStateStore.folder;
        string previousToken = TunnelSharedStates.tokenOverride;
        var previousSend = TunnelSharedStates.send;
        string folder = Path.Combine(Application.temporaryCachePath, "SharedStatesRead");
        lastReport = "reading";
        try
        {
            TunnelStateStore.folder = folder;
            TunnelSharedStates.tokenOverride = "";
            TunnelSharedStates.send = TunnelSharedStates.SendWithUnity;
            TunnelSharedStates.ResetForTests();
            await TunnelSharedStates.Refresh(true);
            var items = TunnelSharedStates.Everything();
            lastReport = $"[SaveState] {TunnelSharedStates.repositoryName}: {items.Count} shared ({string.Join(", ", items.Select(i => $"{i.name} {i.info}"))}); status: {TunnelSharedStates.status}";
            Debug.Log(lastReport);
        }
        catch (Exception e)
        {
            lastReport = $"[SaveState] reading the real repository failed: {e}";
            Debug.LogError(lastReport);
        }
        finally
        {
            TunnelStateStore.folder = previousFolder;
            TunnelSharedStates.tokenOverride = previousToken;
            TunnelSharedStates.send = previousSend;
            TunnelSharedStates.ResetForTests();
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
