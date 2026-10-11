#nullable enable

using Cysharp.Threading.Tasks;
using MajdataViewX.Managers;
using MajProtocol;
using MajProtocol.Serialization;
using MajProtocol.Types;
using MajProtocol.Types.Enums;
using MajProtocol.Types.Request;
using MajProtocol.Types.Response;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;
using static MajdataViewX.Base.MajCtx;
using Debug = UnityEngine.Debug;
using EventType = MajProtocol.Types.Response.EventType;

namespace MajdataViewX
{ 
    public class WsServer : MonoBehaviour
    {
        public static class Endpoint
        {
            public const int PORT = 8083;
            public const string SERVICE_PATH = "/majdata";

            public static string ServerUrl => $"ws://127.0.0.1:{PORT}{SERVICE_PATH}";
        }

        public static readonly ConcurrentQueue<string> MessageQueue = new();
        private WebSocketServer? _webSocket;
        private CancellationToken _lifetimeCancellationToken;

        private void Awake()
        {
            _wsServer = this;
        }

        // 这里是游戏及游戏外部的初始化
        void Start()
        {
            QualitySettings.vSyncCount = 1;

            _webSocket = new WebSocketServer(IPAddress.Any, Endpoint.PORT);
            _webSocket.AddWebSocketService<MajdataWsService>(Endpoint.SERVICE_PATH);
            _webSocket.Start();
            _lifetimeCancellationToken = this.GetCancellationTokenOnDestroy();
            ProcessQueue(_lifetimeCancellationToken).Forget();
        }

        private async UniTaskVoid ProcessQueue(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (MessageQueue.TryDequeue(out var json))
                    {
                        while (_playManager == null ||
                               PlayManager.Summary.State == ViewStatus.Busy)
                            await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);

                        await HandleMessageAsync(json);
                    }
                    else
                    {
                        await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task HandleMessageAsync(string json)
        {
            try
            {
                var reqN = MajProtocolSerializer.Deserialize<RequestEnvelope>(json);
                if (reqN is null)
                {
                    Error("empty or invalid json");
                    Debug.LogError($"request failed: invalid json");
                    return;
                }

                var req = reqN.Value;
                switch (req.RequestType)
                {
                    case RequestType.Reset: // TODO
                        Debug.Log("request finished: Reset");
                        break;
                    case RequestType.Setting:
                        {
                            var payload = (req.RequestData as JObject)?.ToObject<SettingsPayload>()
                                          ?? new SettingsPayload();
                            _playManager.Setting(
                                payload.ViewSetting ?? new(),
                                payload.VolumeSetting ?? new());
                            Response(ResponseType.Ok, PlayManager.Summary);
                            Debug.Log("request finished: Setting");
                            break;
                        }
                    case RequestType.Load:
                        {
                            var loadReq = MajProtocolSerializer.DeserializeRequestPayload<LoadRequest>(req);
                            await _playManager.LoadAsync(
                                loadReq?.TrackPath ?? string.Empty,
                                loadReq?.ImagePath ?? string.Empty,
                                loadReq?.VideoPath);
                            BroadcastEvent(EventType.LoadOk, PlayManager.Summary);
                            Response(ResponseType.Ok, PlayManager.Summary);
                            Debug.Log("request finished: Load");
                            break;
                        }
                    case RequestType.Parse:
                        {
                            // ParseRequest（v1.2 起）含谱面 + 曲目 metadata，一次提交、一次加载。
                            var parseReq = MajProtocolSerializer.DeserializeRequestPayload<ParseRequest>(req);
                            await _playManager.ParseAsync(
                                parseReq?.SimaiFumen,
                                parseReq?.ClockCount ?? 0,
                                parseReq?.Offset ?? 0f,
                                parseReq?.Title ?? string.Empty,
                                parseReq?.Artist ?? string.Empty,
                                parseReq?.Level ?? string.Empty,
                                parseReq?.Designer ?? string.Empty,
                                parseReq?.Difficulty ?? 0);
                            Response(ResponseType.Ok, PlayManager.Summary);
                            Debug.Log("request finished: Parse");
                            break;
                        }
                    case RequestType.Play:
                        {
                            var playReqN = MajProtocolSerializer.DeserializeRequestPayload<PlayRequest>(req);
                            if (playReqN is null)
                            {
                                Error("empty play payload");
                                return;
                            }

                            var playReq = playReqN.Value;
                            var prevPlayState = PlayManager.Summary.State;
                            await _playManager.PlayAsync(
                                playReq.PlayMode, playReq.StartAt, playReq.Speed,
                                playReq.MaidataPath ?? string.Empty);
                            // 首启 (Loaded→Playing) 发 PlayStarted；恢复 (Paused→Playing) 发 PlayResumed。
                            if (PlayManager.Summary.State == ViewStatus.Playing)
                            {
                                var evt = prevPlayState == ViewStatus.Paused
                                    ? EventType.PlayResumed
                                    : EventType.PlayStarted;
                                BroadcastEvent(evt, PlayManager.Summary);
                            }
                            Response(ResponseType.Ok, PlayManager.Summary);
                            Debug.Log("request finished: Play");
                            break;
                        }
                    case RequestType.Pause:
                        if (_screenRecorder.IsRecording) break;
                        await _playManager.PauseAsync();
                        BroadcastEvent(EventType.PlayPaused, PlayManager.Summary);
                        Response(ResponseType.Ok, PlayManager.Summary);
                        Debug.Log("request finished: Pause");
                        break;
                    case RequestType.Resume:
                        await _playManager.ResumeAsync();
                        BroadcastEvent(EventType.PlayResumed, PlayManager.Summary);
                        Response(ResponseType.Ok, PlayManager.Summary);
                        Debug.Log("request finished: Resume");
                        break;
                    case RequestType.Stop:
                        await _playManager.StopAsync();
                        BroadcastEvent(EventType.PlayStopped, PlayManager.Summary);
                        Response(ResponseType.Ok, PlayManager.Summary);
                        Debug.Log("request finished: Stop");
                        break;
                    case RequestType.State:
                        Response(ResponseType.Ok, PlayManager.Summary);
                        Debug.Log("request finished: State");
                        break;
                    default:
                        Error("Not Supported");
                        Debug.LogError("request failed: Not Supported");
                        break;
                }
            } catch (Exception ex)
            {
                Error(ex);
            }
        }

        // for self stopping without request
        public void SendStopResponse()
        {
            Response(ResponseType.Ok, PlayManager.Summary);
        }

        /// <summary>
        /// 序列化并广播一个 ResponseEnvelope。
        /// <paramref name="summary"/> 走 ResponseData；协议契约规定错误信息塞在 Summary.ErrMsg，
        /// 因此 <paramref name="error"/> 只用于本地日志和故障注入。
        /// </summary>
        private void Response(ResponseType type, ViewSummary? summary = null, string? error = null)
        {
            var rsp = new ResponseEnvelope
            {
                ProtocolVersion = ProtocolVersion.Current,
                ResponseType = type,
                ResponseData = summary is null
                    ? null
                    : JToken.FromObject(summary, JsonSerializer.Create(MajProtocolSerializer.DefaultSettings)),
            };
            if (error is not null)
                Debug.LogWarning($"[WsServer] {type} error: {error}");

            // websocket-sharp Broadcast(byte[]) 走二进制帧。保留与老客户端兼容。
            var json = MajProtocolSerializer.Serialize(rsp);
            _webSocket?.WebSocketServices[Endpoint.SERVICE_PATH].Sessions.
                Broadcast(Encoding.UTF8.GetBytes(json));
        }

        /// <summary>
        /// 主动推送一个 Event 响应。ResponseData 是 <see cref="EventResponse"/>。
        /// summary 为 null 时 EventResponse.Summary 也为 null。
        /// </summary>
        private void BroadcastEvent(EventType eventType, ViewSummary? summary = null)
        {
            var evt = new EventResponse
            {
                EventType = eventType,
                Summary = summary,
            };
            var rsp = new ResponseEnvelope
            {
                ProtocolVersion = ProtocolVersion.Current,
                ResponseType = ResponseType.Event,
                ResponseData = JToken.FromObject(evt, JsonSerializer.Create(MajProtocolSerializer.DefaultSettings)),
            };
            var json = MajProtocolSerializer.Serialize(rsp);
            _webSocket?.WebSocketServices[Endpoint.SERVICE_PATH].Sessions.
                Broadcast(Encoding.UTF8.GetBytes(json));
        }
        
        public void Error<T>(T exception) where T : Exception
        {
            // 错误消息统一通过 ViewSummary.ErrMsg 上报。PlayManager 已在其 catch 里把 _errMsg / _state
            // 设置为 Exception.ToString / ViewStatus.Error，所以这里只需发送一份含该状态的 summary。
            Debug.LogException(exception);
            Response(ResponseType.Error, PlayManager.Summary);
        }

        public void Error(string errMsg)
        {
            Debug.LogError($"[WsServer] {errMsg}");
            // 客户端无法直接拿到 error 字符串（ResponseEnvelope 没有 Error 字段），
            // 走协议约定的 ViewSummary.ErrMsg 通道：构造一个临时 summary 把它带过去。
            var summary = new ViewSummary
            {
                State = ViewStatus.Error,
                ErrMsg = errMsg,
            };
            Response(ResponseType.Error, summary);
        }

        private void OnDestroy()
        {
            if (_webSocket is null) return;
            _webSocket.RemoveWebSocketService(Endpoint.SERVICE_PATH);
            _webSocket.Stop();
        }
    }

    public class MajdataWsService : WebSocketBehavior
    {
        protected override void OnMessage(MessageEventArgs e)
        {
            // 文本帧为 JSON 请求；二进制帧按 UTF-8 解码为 JSON（兼容老客户端误发二进制的情况）
            var json = e.IsBinary ? Encoding.UTF8.GetString(e.RawData) : e.Data;
            if (string.IsNullOrEmpty(json))
                return;

            WsServer.MessageQueue.Enqueue(json);
        }
    }

    /// <summary>
    /// Setting 通道的私有 payload：MajProtocol 没规定 SettingRequest 形状，按决策走 JObject 自由形式。
    /// 客户端约定 <c>requestData</c> 必须包含 <c>viewSetting</c> + <c>volumeSetting</c> 两个属性；
    /// 缺失字段视为默认值。
    /// </summary>
    internal sealed class SettingsPayload
    {
        public MajdataViewX.Types.MajSetting.MajViewSetting? ViewSetting { get; set; }
        public MajdataViewX.Types.MajSetting.MajVolumeSetting? VolumeSetting { get; set; }
    }
}