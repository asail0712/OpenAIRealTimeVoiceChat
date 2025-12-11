using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using UnityEngine;

namespace XPlan.OpenAI
{
    public static class AIRealTimeTypes
    {
        public const string Start                           = "AIRealTime.Start";                    // server -> client
        public const string Finish                          = "AIRealTime.Finish";                   // server -> client
        public const string Logging                         = "AIRealTime.Logging";                  // server -> client

        public const string Send                            = "AIRealTime.Send";                     // client -> server
        public const string InterruptReceive                = "AIRealTime.InterruptReceive";         // client -> server
        public const string ReceiveAssistantAudio           = "AIRealTime.ReceiveAssistantAudio";    // server -> client
        public const string ReceiveAssistantTextDelta       = "AIRealTime.ReceiveAssistantTextDelta";// server -> client
        public const string ReceiveAssistantTextDone        = "AIRealTime.ReceiveAssistantTextDone"; // server -> client
        public const string ReceiveUserTextDelta            = "AIRealTime.ReceiveUserTextDelta";     // server -> client
        public const string ReceiveUserTextDone             = "AIRealTime.ReceiveUserTextDone";      // server -> client        
    }

    // 依照Server定義決定
    public class AIMessage
    {
        public string Type { get; set; }        = string.Empty;    
        public object Payload { get; set; }     = null; // Base64 編碼的 PCM16 音訊資料 或是文字資料
    }

    public enum DebugLevel
    {
        Log,
        Warning,
        Error,
    }

    public class RealTimeChatClient : MonoBehaviour, IAIRealTimeChat
    {
        [Tooltip("Mic device name. Leave empty to use default device.")]
        public string microphoneDevice = string.Empty; // 麥克風 (2- Usb Audio Device)

        [Header("Playback")]
        public AudioSource playbackSource; // attach an AudioSource (optional for TTS playback)

        [Header("Audio Settings")]
        [Tooltip("Preferred sample rate for mic capture. Actual mic rate comes from _micClip.frequency.")]
        public int sampleRate = 24000;

        // Send loop flags
        private volatile bool bStreamingMic;

        // ===============================
        // Internals - Mic capture/send
        // ===============================
        private AudioClip _micClip;
        private int _micReadPos;
        private int _clipSamples;
        private int _clipChannels;

        // ===============================
        // Internals - 音訊轉byte[]
        // ===============================
        private Channel<byte[]> _txChan;
        private const int TxFrameMinMs = 20;   // 20–40ms
        private const int TxFrameMaxMs = 40;
        private int _bytesPerMs => (sampleRate /* 24000 */ * 2 /*pcm16*/ ) / 1000;

        // ===============================
        // Internals - RX/playback
        // ===============================
        private readonly ConcurrentQueue<float> _rxQueue    = new ConcurrentQueue<float>(); // 24k mono float
        private int _srcSampleRate                          = 24000;    // model output when pcm16
        private int _dspSampleRate                          = 48000;    // audio device output
        private float _holdSample;                                      // for 24k→48k duplication
        private int _dupState;                                          // 0/1 alternating

        // temp buffers
        private float[] _floatBuf   = Array.Empty<float>(); // multi-channel
        private float[] _monoBuf    = Array.Empty<float>();  // mono
        private byte[] _pcmBuf      = Array.Empty<byte>();   // PCM16 mono

        // ===============================
        // WS Client
        // ===============================
        // ==== 配置這裡 ====
        [Header("WebSocket")]
        [Tooltip("WebSocket 伺服器位址，例如 ws://127.0.0.1:5000/ws 或 wss://your.host/ws")]
        public string wsUrl = "ws://127.0.0.1:5000/ws";

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _sendLock        = new SemaphoreSlim(1, 1);
        private readonly ConcurrentQueue<Action> _main  = new ConcurrentQueue<Action>(); // 主執行緒回撥佇列

        private void EmitOnMain(Action a) { if (a != null) _main.Enqueue(a); }

        // =============== Connection Callbacks ===============
        public event Action onConnected;            // 連線成功
        public event Action<string> onConnectError; // 連線失敗 / 中斷 / 例外

        // ===============================
        // Internals - Mic capture/send
        // ===============================
        public event Action<string> userTextDelta;
        public event Action<string> aiTextDelta;
        public event Action<string> userTextDone;
        public event Action<string> aiTextDone;

        // =============== Websocket Header ===============
        private Dictionary<string, string> headerDict = new Dictionary<string, string>();
        public void AddHeader(string key, string value)
        {
            if(headerDict.ContainsKey(key))
            {
                headerDict[key] = value;
            }
            else
            {
                headerDict.Add(key, value);
            }
        }

        private void Update()
        {
            while (_main.TryDequeue(out var a))
            {
                try { a?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
            }
        }

        private void Awake()
        {
            if (!playbackSource)
            {
                playbackSource              = gameObject.AddComponent<AudioSource>();
                playbackSource.playOnAwake  = true;
                playbackSource.loop         = true;   // feed audio via OnAudioFilterRead
                playbackSource.spatialBlend = 0f;
            }

            // sample rate監控與設定
            _dspSampleRate = AudioSettings.outputSampleRate;
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigChanged;
        }

        private async void Start()
        {
            // 連 WebSocket
            await ConnectAsync();
        }

        private void OnDestroy()
        {
            if (_micClip != null && Microphone.IsRecording(microphoneDevice))
            {
                Microphone.End(microphoneDevice);
            }

            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigChanged;

            try { _cts?.Cancel(); } catch { }
            try { _ws?.Abort(); } catch { }
            try { _ws?.Dispose(); } catch { }
        }

        // ===== 在 ConnectAsync 成功後 or Awake/Start 初始化 =====
        private void InitTxChannel()
        {
            // 32 個 frame 緩衝，約 0.64~1.28 秒（以 20~40ms 計）
            _txChan = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(32)
                {
                    SingleReader    = true,
                    SingleWriter    = true,
                    FullMode        = BoundedChannelFullMode.DropOldest // 核心：滿載丟最舊
                });

            // 專責送出者（單 reader）
            _ = Task.Run(async () =>
            {
                try
                {
                    // 持續讀取_txChan 若是讀到資料就送出
                    await foreach (var frame in _txChan.Reader.ReadAllAsync(_cts.Token))
                    {
                        await SendAsync(new ArraySegment<byte>(frame, 0, frame.Length),
                                        WebSocketMessageType.Binary)
                              .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { /* 正常關閉 */ }
                catch (Exception ex) { Debug.LogWarning($"[TX] Sender error: {ex.Message}"); }
            });
        }

        private void OnAudioConfigChanged(bool deviceWasChanged)
        {
            _dspSampleRate = AudioSettings.outputSampleRate;
            Debug.Log($"[Audio] DSP sampleRate={_dspSampleRate}, deviceChanged={deviceWasChanged}");
        }

        // =============== UI Handlers ===============

        // 點一下：切換持續收音
        public void OnMicClicked()
        {
            if (bStreamingMic)
                StopRecord();
            else
                StartRecord();
        }
        public void StartRecord()
        {
            MicStartButton();
        }

        public void StopRecord()
        {
            MicStopButton();
        }

        // =============== interrupt ai talk ================
        public async Task InterruptChat()
        {
            // 中斷處理
            playbackSource.enabled = false;

            await SendInterruptAsync(); // 通知伺服器中斷目前的 AI 播放
        }

        // =============== Realtime Callbacks ===============

        private void HandleAIResposeStart()
        {
            playbackSource.enabled = true;
            _rxQueue.Clear();
        }

        private void HandleAIResposeFinish()
        {
            //playbackSource.enabled = false;
        }

        private void HandleAITranscript(string text)
        {
            aiTextDelta?.Invoke(text);
        }

        private void HandleUserTranscript(string text)
        {
            userTextDelta?.Invoke(text);
        }

        private void HandleAITranscriptDone(string text)
        {
            aiTextDone?.Invoke(text);
        }

        private void HandleUserTranscriptDone(string text)
        {
            userTextDone?.Invoke(text);
        }

        private void HandleAIAudio(byte[] bytes)
        {
            int sampleCount = bytes.Length / 2;
            var block       = new float[sampleCount];

            for (int i = 0, si = 0; i < bytes.Length; i += 2, si++)
            {
                short s     = (short)(bytes[i] | (bytes[i + 1] << 8));
                block[si]   = s / 32768f; // mono 24k
            }

            for (int i = 0; i < block.Length; i++)
            {
                _rxQueue.Enqueue(block[i]);
            }
        }

        private void HandleAILog(DebugLevel lv, string logStr)
        {
            switch (lv)
            {
                case DebugLevel.Log:
                    Debug.Log(logStr);
                    break;
                case DebugLevel.Warning:
                    Debug.LogWarning(logStr);
                    break;
                case DebugLevel.Error:
                    Debug.LogError(logStr);
                    break;
            }
        }

        // =============== Helpers ===============

        // ===============================
        // Mic controls
        // ===============================

        [ContextMenu("MIC: Start")]
        public void MicStartButton()
        {
            StartMic();
            _ = SendMicLoopAsync();
        }

        [ContextMenu("MIC: Stop")]
        public void MicStopButton()
        {
            bStreamingMic = false;
            if (_micClip != null && Microphone.IsRecording(microphoneDevice))
            {
                Microphone.End(microphoneDevice);
            }
            Debug.Log("[Mic] Stop pressed");
        }

        private void StartMic()
        {
            if (Microphone.devices.Length == 0)
            {
                Debug.LogError("No microphone devices found.");
                return;
            }

            if (string.IsNullOrEmpty(microphoneDevice))
            {
                microphoneDevice = Microphone.devices[0];
            }

            _micClip = Microphone.Start(microphoneDevice, true, 10, sampleRate);

            while (Microphone.GetPosition(microphoneDevice) <= 0)
            { }

            _clipSamples    = _micClip.samples;
            _clipChannels   = _micClip.channels;
            _micReadPos     = 0;

            // allocate small; will be resized in loop as needed
            _floatBuf       = new float[1];
            _monoBuf        = new float[1];
            _pcmBuf         = new byte[1];

            bStreamingMic   = true;

            playbackSource.Play();

            Debug.Log($"[Mic] Device={microphoneDevice}, reqRate={sampleRate}, actualRate={_micClip.frequency}, samples={_clipSamples}, ch={_clipChannels}");
        }

        private async Task SendMicLoopAsync()
        {
            if (_micClip == null)
            {
                Debug.LogError("Mic not started.");
                return;
            }

            if (!IsConnected())
            {
                Debug.LogError("WebSocket not connected.");
                return;
            }

            int effectiveRate   = (_micClip.frequency > 0) ? _micClip.frequency : sampleRate;

            // 建立累積器（byte）來 coalesce 微片段
            var pending         = new byte[8192];
            int pendingLen      = 0;

            int minBytes        = _bytesPerMs * TxFrameMinMs; // 20ms = 960 bytes @24k
            int maxBytes        = _bytesPerMs * TxFrameMaxMs; // 40ms = 1920 bytes @24k

            // 暖機.. 等累積到固定時間(0.15s) 再去讀取資料
            int warmupNeeded    = (int)(effectiveRate * 0.15f);

            while (true)
            {
                int pos         = Microphone.GetPosition(microphoneDevice);
                int available   = (_micReadPos <= pos) ? (pos - _micReadPos) : (pos + _clipSamples - _micReadPos);

                if (available >= warmupNeeded) break;

                await Task.Delay(10);
            }

            // 開始讀取麥克風資料
            while (bStreamingMic && IsConnected())
            {
                int micPos = Microphone.GetPosition(microphoneDevice);
                if (micPos < 0)
                {
                    await Task.Yield(); continue;
                }

                // 因為是使用環形buff 所以用來計算可用樣本數、決定本次要讀多少
                int toSend = (_micReadPos <= micPos) ? (micPos - _micReadPos) : (micPos + _clipSamples - _micReadPos);
                if (toSend <= 0)
                {
                    await Task.Delay(8); continue;
                }

                // 準備一次要讀的 buffer
                // 乘上_clipChannels是考慮多聲道
                int neededFloats = toSend * _clipChannels;
                if (_floatBuf == null || _floatBuf.Length != neededFloats)
                {
                    _floatBuf = new float[neededFloats];
                }

                // 從環形音訊中取出資料，含「不繞」與「繞尾」兩種情況
                if (_micReadPos + toSend <= _clipSamples)
                {
                    _micClip.GetData(_floatBuf, _micReadPos);
                }
                else
                {
                    int firstPart   = _clipSamples - _micReadPos;
                    int secondPart  = toSend - firstPart;
                    var a           = new float[firstPart * _clipChannels];
                    var b           = new float[secondPart * _clipChannels];

                    _micClip.GetData(a, _micReadPos);
                    _micClip.GetData(b, 0);

                    Array.Copy(a, 0, _floatBuf, 0, a.Length);
                    Array.Copy(b, 0, _floatBuf, a.Length, b.Length);
                }

                // downmix → mono 把多聲道壓成單聲道
                if (_monoBuf == null || _monoBuf.Length < toSend)
                {
                    _monoBuf = new float[toSend];
                }

                if (_clipChannels == 1)
                {
                    Array.Copy(_floatBuf, 0, _monoBuf, 0, toSend);
                }
                else
                {
                    for (int i = 0; i < toSend; i++)
                    {
                        double acc = 0; int baseIdx = i * _clipChannels;
                        for (int ch = 0; ch < _clipChannels; ch++)
                        {
                            acc += _floatBuf[baseIdx + ch];
                        }
                        _monoBuf[i] = (float)(acc / _clipChannels);
                    }
                }

                // float [-1,1] → PCM16 LE
                // 每個 sample 在 16-bit PCM 中占 2 bytes (16 bits)
                int byteCount = toSend * 2;

                if (_pcmBuf == null || _pcmBuf.Length < byteCount)
                {
                    _pcmBuf = new byte[byteCount];
                }

                for (int i = 0, b = 0; i < toSend; i++, b += 2)
                {
                    float f         = Mathf.Clamp(_monoBuf[i], -1f, 1f);
                    short s         = (short)Mathf.RoundToInt(f * 32767f);
                    _pcmBuf[b]      = (byte)(s & 0xFF);
                    _pcmBuf[b + 1]  = (byte)((s >> 8) & 0xFF);
                }

                // 更新讀取指標（環形前進）
                _micReadPos = (_micReadPos + toSend) % _clipSamples;

                // 累積資料到 pending（自動擴容）
                if (pendingLen + byteCount > pending.Length)
                {
                    Array.Resize(ref pending, Math.Max(pending.Length * 2, pendingLen + byteCount));
                }

                Buffer.BlockCopy(_pcmBuf, 0, pending, pendingLen, byteCount);
                pendingLen += byteCount;

                // 若不足 20ms，繼續累積（也避免 <10ms 的碎片被送出）
                if (pendingLen < minBytes)
                {
                    continue;
                }

                // 一次可以切到 20~40ms 的塊丟進 channel
                while (pendingLen >= minBytes)
                {
                    int sendBytes   = Math.Min(pendingLen, maxBytes);
                    var frame       = new byte[sendBytes];
                    Buffer.BlockCopy(pending, 0, frame, 0, sendBytes);

                    // 丟進 bounded channel（滿了會自動丟最舊）
                    // TryWrite 幾乎總是成功；若偶爾失敗，用 WriteAsync 也可
                    if (!_txChan.Writer.TryWrite(frame))
                        await _txChan.Writer.WriteAsync(frame, _cts.Token);

                    // 左移剩餘
                    int left = pendingLen - sendBytes;
                    if (left > 0)
                        Buffer.BlockCopy(pending, sendBytes, pending, 0, left);
                    pendingLen = left;

                    // 若剩下 <10ms，就繼續累積，不再細碎送
                    if (pendingLen < _bytesPerMs * 10) break;
                }
            }
        }

        // ===============================
        // Playback (audio thread)
        // ===============================
        void OnAudioFilterRead(float[] data, int channels)
        {
            int dstRate = _dspSampleRate;

            if (dstRate == _srcSampleRate * 2)
            {
                // Fast path: 24k -> 48k (duplicate every sample)
                for (int i = 0; i < data.Length; i += channels)
                {
                    float sample;
                    if (_dupState == 0)
                    {
                        if (!_rxQueue.TryDequeue(out sample))
                        {
                            sample = 0f;
                        }
                        _holdSample = sample; _dupState = 1;
                    }
                    else
                    {
                        sample = _holdSample; _dupState = 0;
                    }
                    for (int c = 0; c < channels; c++) data[i + c] = sample;
                }
            }
            else
            {
                // Fallback: simple hold-based resampling (cheap and stable for speech)
                double step = _srcSampleRate / Math.Max(1, dstRate);
                double acc  = 0.0;
                for (int i = 0; i < data.Length; i += channels)
                {
                    while (acc <= 0.0)
                    {
                        if (_rxQueue.TryDequeue(out _holdSample)) { }
                        acc += 1.0;
                    }
                    acc -= step;
                    for (int c = 0; c < channels; c++) data[i + c] = _holdSample;
                }
            }
        }

        // ===============================
        // WebSocket client
        // ===============================
        public bool IsConnected() => _ws != null && _ws.State == WebSocketState.Open;

        private async Task ConnectAsync()
        {
            _cts    = new CancellationTokenSource();
            _ws     = new ClientWebSocket();

            InitHeader();

            try
            {
                var uri = new Uri(wsUrl);
                await _ws.ConnectAsync(uri, _cts.Token).ConfigureAwait(false);
                Debug.Log($"[WS] Connected: {wsUrl}");

                // 觸發 callback（排回主執行緒）
                EmitOnMain(() => onConnected?.Invoke());

                // 啟動收訊循環
                _ = Task.Run(ReceiveLoopAsync);

                InitTxChannel();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WS] Connect failed: {ex.Message}");

                // 觸發 callback（排回主執行緒）
                EmitOnMain(() => onConnectError?.Invoke(ex.Message));
            }
        }

        private void InitHeader()
        {
            foreach(var kvp in headerDict)
            {
                _ws.Options.SetRequestHeader(kvp.Key, kvp.Value);
            }            
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];

            while (IsConnected() && !_cts.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                int offset = 0;

                try
                {
                    do
                    {
                        var seg = new ArraySegment<byte>(buffer, offset, buffer.Length - offset);
                        result  = await _ws.ReceiveAsync(seg, _cts.Token).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", _cts.Token).ConfigureAwait(false);
                            return;
                        }

                        offset += result.Count;

                        // 防護：訊息過大則擴容
                        if (offset >= buffer.Length)
                        {
                            Array.Resize(ref buffer, buffer.Length * 2);
                        }

                    } while (!result.EndOfMessage);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[WS] Receive error: {ex.Message}");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, offset);
                    HandleIncomingJson(json);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // 目前後端是把音訊包在文字 JSON 的 Payload（Base64），理論上不會傳 Binary。
                    // 若改成 Binary，這裡可以直接轉 float 塞 _rxQueue。
                }
            }
        }

        private void HandleIncomingJson(string json)
        {
            AIMessage env = null;
            try
            {
                env = JsonConvert.DeserializeObject<AIMessage>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WS] Invalid JSON: {ex.Message}");
                return;
            }

            if (env == null || string.IsNullOrWhiteSpace(env.Type)) return;

            switch (env.Type)
            {
                case AIRealTimeTypes.Start:
                    EmitOnMain(() => HandleAIResposeStart());
                    break;

                case AIRealTimeTypes.Finish:
                    EmitOnMain(() => HandleAIResposeFinish());
                    break;

                case AIRealTimeTypes.ReceiveAssistantTextDelta:
                    {
                        var payload = env.Payload ?? "";
                        EmitOnMain(() => HandleAITranscript(payload.ToString()));
                        break;
                    }
                case AIRealTimeTypes.ReceiveAssistantTextDone:
                    {
                        var payload = env.Payload ?? "";
                        EmitOnMain(() => HandleAITranscriptDone(payload.ToString()));
                        break;
                    }

                case AIRealTimeTypes.ReceiveUserTextDelta:
                    {
                        var payload = env.Payload ?? "";
                        EmitOnMain(() => HandleUserTranscript(payload.ToString()));
                        break;
                    }
                case AIRealTimeTypes.ReceiveUserTextDone:
                    {
                        var payload = env.Payload ?? "";
                        EmitOnMain(() => HandleUserTranscriptDone(payload.ToString()));
                        break;
                    }

                case AIRealTimeTypes.ReceiveAssistantAudio:
                    {
                        var payload = env.Payload ?? "";
                        try
                        {
                            var bytes = Convert.FromBase64String(payload.ToString());
                            // 這裡在音訊執行緒上做轉換較便宜，但為簡化，直接主緒列隊後再丟 RX 也可。
                            EmitOnMain(() => HandleAIAudio(bytes));
                        }
                        catch { }
                        break;
                    }
                case AIRealTimeTypes.Logging:
                    {
                        var payload = env.Payload ?? "";
                        EmitOnMain(() => HandleAILog(DebugLevel.Log, payload.ToString()));
                        break;
                    }

                default:
                    // 其他型別視需要擴充
                    break;
            }
        }

        private async Task SendAsync(ArraySegment<byte> seg, WebSocketMessageType type)
        {
            if (!IsConnected()) return;

            await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);

            try
            {
                if (IsConnected() && !_cts.IsCancellationRequested)
                {
                    await _ws.SendAsync(seg, type, true, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WS] Send error: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public Task SendAudioAsync(byte[] buffer, int count)
        {
            if (buffer == null || count <= 0) return Task.CompletedTask;
            return SendAsync(new ArraySegment<byte>(buffer, 0, count), WebSocketMessageType.Binary);
        }

        public async Task SendInterruptAsync()
        {
            try
            {
                var msg = new AIMessage { Type = AIRealTimeTypes.InterruptReceive, Payload = "" };
                string json;
                try
                {
                    json = JsonConvert.SerializeObject(msg);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[WS] Serialize error: {ex.Message}");
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(json);

                await SendAsync(bytes, WebSocketMessageType.Text);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WS] Serialize error: {ex.Message}");
            }
        }
    }
}