using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Delphi.Trial
{
    /// <summary>
    /// Owns the Bayesian-optimizer side process and the socket to it.
    /// Speaks mobo.py's protocol directly (NDJSON over TCP :56001 — the
    /// same wire format BO-for-Unity's SocketNetwork uses, verified against
    /// mobo.py with a standalone client before this was written):
    ///
    ///   Unity → python:  {type:"init", config{...}, user{...},
    ///                     parameters:[{key,init:"lo,hi"}],
    ///                     objectives:[{key,init:"lo,hi,minimize"}]}
    ///                    {type:"objectives", values:{key:val,...}}
    ///   python → Unity:  {type:"parameters", values:{key:val,...}}
    ///                    {type:"coverage"|"tempCoverage", value:...}
    ///                    {type:"optimization_finished"}
    ///
    /// Plain C# — no MonoBehaviour. A reader thread parses incoming lines
    /// into a queue the main thread drains; process stdout/stderr are piped
    /// to the Unity console with a [BO] prefix.
    /// </summary>
    public sealed class BoBridge : IDisposable
    {
        public const int Port = 56001;

        private Process _proc;
        private TcpClient _tcp;
        private NetworkStream _stream;
        private Thread _reader;
        private volatile bool _running;

        public readonly ConcurrentQueue<JObject> Incoming = new();
        public bool ProcessAlive => _proc != null && !_proc.HasExited;
        public bool Connected => _tcp != null && _tcp.Connected;

        // ── Process ─────────────────────────────────────────────────────
        /// <summary>Launch mobo.py. `pythonPath` empty → the project-local
        /// venv (BOPythonEnv/bin/python3 next to Assets).</summary>
        public void StartProcess(string pythonPath, string scriptDir, string scriptName)
        {
            if (string.IsNullOrWhiteSpace(pythonPath))
                pythonPath = Path.GetFullPath(Path.Combine(
                    UnityEngine.Application.dataPath, "..", "BOPythonEnv", "bin", "python3"));

            if (!File.Exists(pythonPath))
                throw new FileNotFoundException(
                    $"Python not found at '{pythonPath}'. Create the venv " +
                    "(python3.13 -m venv BOPythonEnv && BOPythonEnv/bin/pip install -r " +
                    "Assets/StreamingAssets/BOData/Installation/requirements.txt) " +
                    "or set the path on the TrialManager.", pythonPath);

            string script = Path.Combine(scriptDir, scriptName);
            if (!File.Exists(script))
                throw new FileNotFoundException($"Optimizer script not found: {script}", script);

            _proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = $"\"{script}\"",
                    WorkingDirectory = scriptDir, // LogData/ lands here, as upstream expects
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            _proc.OutputDataReceived += (_, e) =>
            { if (!string.IsNullOrEmpty(e.Data)) UnityEngine.Debug.Log($"[BO] {e.Data}"); };
            _proc.ErrorDataReceived += (_, e) =>
            { if (!string.IsNullOrEmpty(e.Data)) UnityEngine.Debug.LogWarning($"[BO:err] {e.Data}"); };
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }

        // ── Socket ──────────────────────────────────────────────────────
        /// <summary>Try to connect to the optimizer's server; returns false
        /// if it isn't accepting yet (call again next frame — the python
        /// side needs a few seconds to import torch).</summary>
        public bool TryConnect()
        {
            if (Connected) return true;
            try
            {
                var tcp = new TcpClient();
                var result = tcp.BeginConnect("127.0.0.1", Port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(250))
                {
                    tcp.Close();
                    return false;
                }
                tcp.EndConnect(result);
                _tcp = tcp;
                _stream = tcp.GetStream();
                _running = true;
                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "BO socket reader" };
                _reader.Start();
                return true;
            }
            catch (SocketException)
            {
                return false; // server not up yet
            }
        }

        private void ReadLoop()
        {
            var buf = new byte[4096];
            var sb = new StringBuilder();
            try
            {
                while (_running)
                {
                    int n = _stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));

                    int idx;
                    while ((idx = IndexOfNewline(sb)) >= 0)
                    {
                        string line = sb.ToString(0, idx).TrimEnd('\r');
                        sb.Remove(0, idx + 1);
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try { Incoming.Enqueue(JObject.Parse(line)); }
                        catch (Exception e)
                        {
                            UnityEngine.Debug.LogWarning($"[BoBridge] Bad JSON line from optimizer: {e.Message}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (_running)
                    UnityEngine.Debug.LogWarning($"[BoBridge] Socket reader stopped: {e.Message}");
            }
        }

        private static int IndexOfNewline(StringBuilder sb)
        {
            for (int i = 0; i < sb.Length; i++) if (sb[i] == '\n') return i;
            return -1;
        }

        // ── Sending ─────────────────────────────────────────────────────
        public void Send(JObject obj)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }

        public void SendObjectives(Dictionary<string, float> values)
        {
            var vals = new JObject();
            foreach (var kv in values) vals[kv.Key] = kv.Value;
            Send(new JObject { ["type"] = "objectives", ["values"] = vals });
        }

        // ── Teardown ────────────────────────────────────────────────────
        public void Dispose()
        {
            _running = false;
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            try { _reader?.Join(300); } catch { }
            try
            {
                if (_proc != null && !_proc.HasExited) _proc.Kill();
                _proc?.Dispose();
            }
            catch { }
            _proc = null; _tcp = null; _stream = null; _reader = null;
        }
    }
}
