using System;
using System.Threading.Tasks;

namespace XPlan.OpenAI
{
    public interface IAIRealTimeChat
    {
        //**************** Connect ***************
        Task Connect();
        Task Disconnect();
        void AddHeader(string key, string value);

        event Action onConnected;            // 連線成功
        event Action<string> onConnectError; // 連線失敗 / 中斷 / 例外

        //**************** Trigger ***************
        void OnMicClicked();
        void StartRecord();
        void StopRecord();
        Task InterruptChat();

        //**************** Callback ***************
        event Action aiResponseStart;
        event Action aiResponseFinish;
        event Action<string> userTextDelta;
        event Action<string> aiTextDelta;
        event Action<string> userTextDone;
        event Action<string> aiTextDone;
    }
}
