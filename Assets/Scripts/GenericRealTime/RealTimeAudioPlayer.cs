using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace XPlan.OpenAI
{
    /// <summary>
    /// 專職處理「AI 回傳的 PCM16 音訊」→ 排隊 → 何時播放 的元件
    /// </summary>
    public class RealTimeAudioPlayer : MonoBehaviour
    {
        [Header("Audio Source")]
        [Tooltip("用來播放 AI 語音的 AudioSource")]
        public AudioSource playbackSource;

        [Header("Audio Settings")]
        [Tooltip("AI 回傳的 PCM16 來源取樣率")]
        public int srcSampleRate = 24000;

        private readonly ConcurrentQueue<float> _rxQueue = new ConcurrentQueue<float>();

        private int _dspSampleRate;
        private float _holdSample;
        private int _dupState;

        private bool _isPlaying = false;

        // ===== 新增：這一輪播放用的 callback =====
        private Action _onPlaybackCompleted;
        private volatile bool _invokeCallbackOnMainThread   = false;

        // ===== 播放結束檢測用 =====
        private bool _responseFinishFlag                    = false;    // OnAIResponseFinish 設定
        private bool _playbackDoneNotified                  = false;    // 避免重複觸發

        // ===== 備份語音資料 =====
        private float[] _latestFrame;
        private int _latestChannels;
        private readonly object _lockObj                    = new();

        private void Awake()
        {
            if (!playbackSource)
            {
                playbackSource              = gameObject.AddComponent<AudioSource>();
                playbackSource.playOnAwake  = false;
                playbackSource.loop         = true;   // 由 OnAudioFilterRead 餵資料
                playbackSource.spatialBlend = 0f;
            }

            _dspSampleRate                              = AudioSettings.outputSampleRate;
            AudioSettings.OnAudioConfigurationChanged   += OnAudioConfigChanged;
        }

        private void Update()
        {
            if (_invokeCallbackOnMainThread)
            {
                playbackSource.Stop();
                _onPlaybackCompleted?.Invoke();

                _invokeCallbackOnMainThread = false;
                _onPlaybackCompleted        = null;

            }
        }

        private void OnDestroy()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigChanged;
        }

        private void OnAudioConfigChanged(bool deviceWasChanged)
        {
            _dspSampleRate = AudioSettings.outputSampleRate;
            Debug.Log($"[AudioPlayer] DSP sampleRate={_dspSampleRate}, deviceChanged={deviceWasChanged}");
        }

        /// <summary>
        /// WebSocket 收到 AI 語音時呼叫：塞進 queue，但不一定立刻播放
        /// </summary>
        public void EnqueuePcm16(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;

            int sampleCount = bytes.Length / 2;
            for (int i = 0, si = 0; i < bytes.Length; i += 2, si++)
            {
                short s = (short)(bytes[i] | (bytes[i + 1] << 8));
                float f = s / 32768f;
                _rxQueue.Enqueue(f);
            }
        }

        /// <summary>
        /// 開始播放（如果 queue 已經有資料，就會直接開始放）
        /// </summary>
        public void StartPlay(Action onFinished = null)
        {
            _onPlaybackCompleted        = onFinished;

            _isPlaying                  = true;
            _playbackDoneNotified       = false;
            _invokeCallbackOnMainThread = false;

            if (!playbackSource.isPlaying)
            {
                playbackSource.Play();
            }
        }

        /// <summary>
        /// 暫停播放（不清 buffer）
        /// </summary>
        public void PausePlay()
        {
            _isPlaying = false;
        }

        /// <summary>
        /// 停止並清空 buffer
        /// </summary>
        public void StopAndClear()
        {
            _isPlaying = false;
            ClearQueue();
        }

        /// <summary>
        /// 只清空 buffer
        /// </summary>
        public void ClearQueue()
        {
            while (_rxQueue.TryDequeue(out _)) { }
        }

        /// <summary>
        /// 例如 AIRealTimeTypes.Start 時可呼叫，重置 queue，但不強制 auto-play
        /// </summary>
        public void OnAIResponseStart(bool autoPlay = false)
        {
            ClearQueue();
            _dupState                   = 0;
            _holdSample                 = 0f;

            _responseFinishFlag         = false;
            _playbackDoneNotified       = false;
            _invokeCallbackOnMainThread = false;

            if (autoPlay)
            {
                StartPlay();
            }
        }

        /// <summary>
        /// 例如 AIRealTimeTypes.Finish 時可呼叫
        /// </summary>
        public void OnAIResponseFinish(bool stopImmediately = false)
        {
            _responseFinishFlag = true;

            if (stopImmediately)
            {
                StopAndClear();

                // 視需求決定要不要直接當作「播放結束」
                _invokeCallbackOnMainThread = true;
            }

            // 否則就讓已經在 queue 裡的放完
        }

        public bool TryGetLatestFrame(out float[] frame, out int channels)
        {
            lock (_lockObj)
            {
                if (_latestFrame == null)
                {
                    frame       = null;
                    channels    = 0;
                    return false;
                }

                // 這裡可以選擇「直接回 reference」或「複製一份」，
                // 為了省 GC，建議直接回 reference，外面只讀不改
                frame       = _latestFrame;
                channels    = _latestChannels;
                return true;
            }
        }

        /// <summary>
        /// 給 Unity audio thread 用的 callback
        /// </summary>
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_isPlaying)
            {
                // 不播放時直接清空輸出，避免有殘音
                for (int i = 0; i < data.Length; i++)
                    data[i] = 0f;
                return;
            }

            int dstRate = _dspSampleRate;

            if (dstRate == srcSampleRate * 2)
            {
                // Fast path: 24k -> 48k (duplicate every sample)
                for (int i = 0; i < data.Length; i += channels)
                {
                    float sample;
                    if (_dupState == 0)
                    {
                        if (!_rxQueue.TryDequeue(out sample))
                            sample = 0f;

                        _holdSample = sample;
                        _dupState   = 1;
                    }
                    else
                    {
                        sample      = _holdSample;
                        _dupState   = 0;
                    }

                    for (int c = 0; c < channels; c++)
                        data[i + c] = sample;
                }
            }
            else
            {
                // Fallback: 簡單 hold-based 重取樣
                double step = (double)srcSampleRate / Math.Max(1, dstRate);
                double acc  = 0.0;

                for (int i = 0; i < data.Length; i += channels)
                {
                    while (acc <= 0.0)
                    {
                        if (_rxQueue.TryDequeue(out _holdSample)) { }
                        acc += 1.0;
                    }
                    acc -= step;

                    for (int c = 0; c < channels; c++)
                        data[i + c] = _holdSample;
                }
            }

            // ⭐⭐ 關鍵：這裡「多做」一件事，把 data 複製出去給主執行緒用 ⭐⭐
            lock (_lockObj)
            {
                if (_latestFrame == null || _latestFrame.Length != data.Length)
                    _latestFrame = new float[data.Length];

                Array.Copy(data, _latestFrame, data.Length);
                _latestChannels = channels;
            }

            // ====== 播放結束偵測 ======
            // 條件：server 已宣告 Finish + queue 已經空 + 還沒通知過
            if (_responseFinishFlag && _rxQueue.IsEmpty && !_playbackDoneNotified)
            {
                _playbackDoneNotified       = true;
                _isPlaying                  = false;
                
                // audio thread 不能直接呼叫 callback，用 flag 丟回 main thread
                _invokeCallbackOnMainThread = true;
            }
        }
    }
}
