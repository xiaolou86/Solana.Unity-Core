using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Solnet.Rpc.Core.Sockets;
using Solnet.Rpc.Messages;
using Solnet.Rpc.Models;

namespace Solnet.Rpc
{
    public class SolanaStreamingRpcClient : StreamingRpcClient
    {
        private int _id;
        private int GetNextId()
        {
            lock (this)
            {
                return _id++;
            }
        }
        
        Dictionary<int, SubscriptionState> unconfirmedRequests = new Dictionary<int, SubscriptionState>();

        Dictionary<int, SubscriptionState> confirmedSubscriptions = new Dictionary<int, SubscriptionState>();

        public SolanaStreamingRpcClient(string url) : base(url)
        {
        }
        
        public SolanaStreamingRpcClient(string url, IWebSocket websocket) : base(url, websocket)
        {
        }

        protected override void HandleNewMessage(Memory<byte> mem)
        {
            Utf8JsonReader asd = new Utf8JsonReader(mem.Span);

            asd.Read();

            string prop = "", method = "";
            int id = -1, intResult = -1;
            bool handled = false;
            bool? boolResult = null;

            while (!handled && asd.Read())
            {
                switch (asd.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        prop = asd.GetString();
                        if (prop == "params")
                        {
                            HandleDataMessage(ref asd, method);
                            handled = true;
                        }
                        break;
                    case JsonTokenType.String:
                        if (prop == "method")
                        {
                            method = asd.GetString();
                        }
                        break;
                    case JsonTokenType.Number:
                        if (prop == "id")
                        {
                            id = asd.GetInt32();
                        }
                        else if (prop == "result")
                        {
                            intResult = asd.GetInt32();
                        }
                        if (id != -1 && intResult != -1)
                        {
                            ConfirmSubscription(id, intResult);
                            handled = true;
                        }
                        break;
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                        if (prop == "result")
                        {
                            boolResult = asd.GetBoolean();
                        }
                        break;
                }
            }

            if (boolResult.HasValue)
            {
                RemoveSubscription(id, boolResult.Value);
            }
        }

        private void RemoveSubscription(int id, bool value)
        {
            SubscriptionState sub;
            lock (this)
            {
                if (!unconfirmedRequests.Remove(id, out sub))
                {
                    // houston, we might have a problem?
                }
            }
            if (value)
            {
                sub?.ChangeState(SubscriptionStatus.Unsubscribed);
            }
            else
            {
                sub?.ChangeState(sub.State, "Subscription doesnt exists");
            }
        }

        #region SubscriptionMapHandling

        private void ConfirmSubscription(int internalId, int resultId)
        {
            SubscriptionState sub;
            lock (this)
            {
                if (unconfirmedRequests.Remove(internalId, out sub))
                {
                    sub.SubscriptionId = resultId;
                    confirmedSubscriptions.Add(resultId, sub);
                }
            }

            sub?.ChangeState(SubscriptionStatus.Subscribed);
        }

        private void AddSubscription(SubscriptionState subscription, int internalId)
        {
            lock (this)
            {
                unconfirmedRequests.Add(internalId, subscription);
            }
        }

        private SubscriptionState RetrieveSubscription(int subscriptionId)
        {
            lock (this)
            {
                return confirmedSubscriptions[subscriptionId];
            }
        }
        #endregion

        private void HandleDataMessage(ref Utf8JsonReader reader, string method)
        {
            JsonSerializerOptions opts = new JsonSerializerOptions() { MaxDepth = 64, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

            switch (method)
            {
                case "accountNotification":
                    var accNotification = JsonSerializer.Deserialize<JsonRpcStreamResponse<AccountInfo>>(ref reader, opts);
                    if (accNotification == null) break;
                    NotifyData(accNotification.Subscription, accNotification.Result);
                    break;
                case "logsNotification":
                    var logsNotification = JsonSerializer.Deserialize<JsonRpcStreamResponse<LogInfo>>(ref reader, opts);
                    if (logsNotification == null) break;
                    NotifyData(logsNotification.Subscription, logsNotification.Result);
                    break;
                case "programNotification":
                    var programNotification = JsonSerializer.Deserialize<JsonRpcStreamResponse<ProgramInfo>>(ref reader, opts);
                    if (programNotification == null) break;
                    NotifyData(programNotification.Subscription, programNotification.Result);
                    break;
                case "signatureNotification":
                    var signatureNotification = JsonSerializer.Deserialize<JsonRpcStreamResponse<string>>(ref reader, opts);
                    if (signatureNotification == null) break;
                    NotifyData(signatureNotification.Subscription, signatureNotification.Result);
                    // remove subscription from map
                    break;
                case "slotNotification":
                    var slotNotification = JsonSerializer.Deserialize<JsonRpcStreamResponse<SlotInfo>>(ref reader, opts);
                    if (slotNotification == null) break;
                    NotifyData(slotNotification.Subscription, slotNotification.Result);
                    break;
                case "rootNotification":
                    var rootNotification = JsonSerializer.Deserialize<JsonRpcStreamResponse<int>>(ref reader, opts);
                    if (rootNotification == null) break;
                    NotifyData(rootNotification.Subscription, rootNotification.Result);
                    break;
            }
        }

        private void NotifyData(int subscription, object data)
        {
            var sub = RetrieveSubscription(subscription);

            sub.HandleData(data);
        }

        public async Task<SubscriptionState> SubscribeAccountInfoAsync(string pubkey, Action<SubscriptionState, ResponseValue<AccountInfo>> callback)
        {
            var sub = new SubscriptionState<ResponseValue<AccountInfo>>(this, SubscriptionChannel.Account, callback, new List<object> { pubkey });

            var msg = new JsonRpcRequest(GetNextId(), "accountSubscribe", new List<object> { pubkey, new Dictionary<string, string> { { "encoding", "base64" } } });

            var json = JsonSerializer.SerializeToUtf8Bytes(msg, new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });


            ReadOnlyMemory<byte> mem = new ReadOnlyMemory<byte>(json);
            await ClientSocket.SendAsync(mem, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

            AddSubscription(sub, msg.Id);
            return sub;
        }

        public SubscriptionState SubscribeAccountInfo(string pubkey, Action<SubscriptionState, ResponseValue<AccountInfo>> callback)
            => SubscribeAccountInfoAsync(pubkey, callback).Result;

        public async Task UnsubscribeAsync(SubscriptionState subscription)
        {
            var req = new JsonRpcRequest(GetNextId(), GetUnsubscribeMethodName(subscription.Channel), new List<object> { subscription.SubscriptionId });

            var json = JsonSerializer.SerializeToUtf8Bytes(req, new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            Console.WriteLine("\n\n[" + DateTime.UtcNow.ToLongTimeString() + "][Received]\n" + Encoding.UTF8.GetString(json, 0, json.Length));

            AddSubscription(subscription, req.Id);

            ReadOnlyMemory<byte> mem = new ReadOnlyMemory<byte>(json);
            await ClientSocket.SendAsync(mem, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }

        private string GetUnsubscribeMethodName(SubscriptionChannel channel) => channel switch
        {
            SubscriptionChannel.Account => "accountUnsubscribe",
            SubscriptionChannel.Logs => "logsUnsubscribe",
            SubscriptionChannel.Program => "programUnsubscribe",
            SubscriptionChannel.Root => "rootUnsubscribe",
            SubscriptionChannel.Signature => "signatureUnsubscribe",
            SubscriptionChannel.Slot => "slotUnsubscribe"
        };

        public void Unsubscribe(SubscriptionState subscription) => UnsubscribeAsync(subscription).Wait();
    }
}