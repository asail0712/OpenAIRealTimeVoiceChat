using UnityEngine;
using UnityEngine.UI;

namespace XPlan.OpenAI
{
    public class RealTimeChatHandler : MonoBehaviour
    {
        [SerializeField] private Text aiSpeechText;
        [SerializeField] private Text userSpeechText;
        [SerializeField] private Button speakBtn;
        [SerializeField] private Button interruptBtn;
        [SerializeField] private Text speakText;

        private IAIRealTimeChat aiRealTimeChat;
        private bool bClick = false;

        void Awake()
        {
            RealTimeChatClient chatClient   = GetComponent<RealTimeChatClient>();
            aiRealTimeChat                  = chatClient;

            this.aiRealTimeChat.userTextDelta   += ReceiveUserTextDelta;
            this.aiRealTimeChat.aiTextDelta     += ReceiveAITextDelta;
            this.aiRealTimeChat.userTextDone    += ReceiveUserTextFinish;
            this.aiRealTimeChat.aiTextDone      += ReceiveAITextFinish;

            this.aiRealTimeChat.onConnected     += OnConnected;
            this.aiRealTimeChat.onConnectError  += OnConnectedError;

            this.aiRealTimeChat.AddHeader("Authorization", $"Bearer xxx");

            speakText.text = "按下說話";

            speakBtn.onClick.AddListener(() => 
            {
                this.aiRealTimeChat.OnMicClicked();

                bClick = !bClick;

                if (bClick)
                    speakText.text = "收音中…(點擊關)";
                else
                    speakText.text = "按下說話";
            });

            interruptBtn.onClick.AddListener(async () =>
            {
                await this.aiRealTimeChat.InterruptChat();
            });
        }
        /*****************
         * 其他(連線流程)
         * ***************/
        private void OnConnected()
        {
            
        }

        private void OnConnectedError(string s)
        {

        }

        /*****************
         * 其他(對話流程)
         * ***************/
        private void ReceiveUserTextDelta(string s)
        {
            Debug.Log(s);
            // 逐字：即時顯示；最終：覆蓋並可加結尾標記
            userSpeechText.text = s;
        }

        private void ReceiveAITextDelta(string s)
        {
            Debug.Log(s);

            aiSpeechText.text = s + "▌"; // 小光標感
        }

        private void ReceiveUserTextFinish(string s)
        {
            
        }

        private void ReceiveAITextFinish(string s)
        {
            
        }
    }
}