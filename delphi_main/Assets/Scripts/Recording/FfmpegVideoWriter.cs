using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Delphi
{
    /// <summary>
    /// One mp4 encode. Raw RGBA frames are pushed from the main thread into
    /// a bounded queue; a background thread streams them into an ffmpeg
    /// subprocess's stdin (project rule: IO never blocks the main thread).
    /// h264/yuv420p output plays back in Unity's VideoPlayer and QuickTime.
    /// </summary>
    public class FfmpegVideoWriter
    {
        private readonly Process _proc;
        private readonly Stream _stdin;
        private readonly Thread _thread;
        private readonly BlockingCollection<byte[]> _queue =
            new BlockingCollection<byte[]>(boundedCapacity: 120);

        private volatile string _lastStderr = "";
        public bool Failed { get; private set; }
        public string OutputPath { get; }

        public FfmpegVideoWriter(string ffmpegPath, string outputPath,
                                 int width, int height, int fps, bool vflip)
        {
            OutputPath = outputPath;
            string filter = vflip ? "-vf vflip " : "";
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments =
                    $"-y -f rawvideo -pix_fmt rgba -s {width}x{height} -r {fps} -i - " +
                    $"{filter}-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p " +
                    $"\"{outputPath}\"",
                UseShellExecute        = false,
                RedirectStandardInput  = true,
                RedirectStandardError  = true, // must be drained or ffmpeg stalls
                CreateNoWindow         = true
            };

            _proc = Process.Start(psi);
            _stdin = _proc.StandardInput.BaseStream;
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) _lastStderr = e.Data; };
            _proc.BeginErrorReadLine();

            _thread = new Thread(WriteLoop) { IsBackground = true, Name = $"ffmpeg {Path.GetFileName(outputPath)}" };
            _thread.Start();
        }

        /// <summary>Queue a frame (RGBA, width*height*4 bytes). If the encoder
        /// can't keep up the frame is dropped rather than stalling the sim —
        /// better a briefly frozen video than a hitch in the experiment.</summary>
        public void Push(byte[] rgba)
        {
            if (Failed || _queue.IsAddingCompleted) return;
            _queue.TryAdd(rgba);
        }

        private void WriteLoop()
        {
            try
            {
                foreach (var frame in _queue.GetConsumingEnumerable())
                    _stdin.Write(frame, 0, frame.Length);
                _stdin.Flush();
            }
            catch (Exception e)
            {
                Failed = true;
                UnityEngine.Debug.LogError(
                    $"[FfmpegVideoWriter] Encode failed for {OutputPath}: {e.Message} " +
                    $"(ffmpeg: {_lastStderr})");
            }
        }

        /// <summary>Stop accepting frames, drain the queue, close stdin and
        /// wait for ffmpeg to finalise the file. Call from the main thread on
        /// StopRecording — bounded waits, no indefinite blocking.</summary>
        public void Finish()
        {
            _queue.CompleteAdding();
            _thread.Join(15000);
            try
            {
                _stdin.Close();
                if (!_proc.WaitForExit(15000)) _proc.Kill();
                _proc.Dispose();
            }
            catch { /* already gone */ }
        }
    }
}
