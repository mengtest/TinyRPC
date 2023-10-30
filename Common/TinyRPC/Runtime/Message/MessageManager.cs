using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using zFramework.TinyRPC.Messages;
using zFramework.TinyRPC.Settings;

namespace zFramework.TinyRPC
{
    // 获取所有的消息处理器解析并缓存
    // 消息处理器注册方式有 2 种：
    // 1. 使用  MessageHandlerProviderAttribute +  MessageHandlerAttribute 标记方法,前者标记类型，后者标记方法
    // 2. 通过 UnityEngine.Component 扩展方法 AddNetworkSignal  注册
    //
    // 约定 MessageHandlerAttribute 只能出现在静态方法上
    public static class MessageManager
    {
        internal static readonly Dictionary<Type, INormalMessageHandler> NormalMessageHandlers = new();
        internal static readonly Dictionary<Type, IRpcMessageHandler> RpcMessageHandlers = new();
        static readonly Dictionary<Type, Type> rpcMessagePairs = new(); // RPC 消息对，key = Request , value = Response
        static readonly Dictionary<int, RpcInfo> rpcInfoPairs = new(); // RpcId + RpcInfo

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        public static void Awake()
        {
            // add ping message and its handler internal 
            RegistPingMessageAndHandlerInternal();
            // regist rpc message pairs must before RegistGeneratedMessageHandlers
            RegistRPCMessagePairs();
            RegistGeneratedMessageHandlers();
            // if user has regist attribute marked handler task , then regist them
            RegistAttributeMarkedHandlerTask();
        }

        public static void RegistAttributeMarkedHandlerTask()
        {
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .Where(v => Array.Exists(TinyRpcSettings.Instance.AssemblyNames, item => v.FullName.StartsWith($"{item},")))
                .SelectMany(v => v.GetTypes())
                .Where(v => v.GetCustomAttribute<MessageHandlerProviderAttribute>() != null);

            // log all types ，count + string.join('\n'
            Debug.Log($"{nameof(MessageManager)}: {types.Count()} 个消息处理器提供者被注册，分别是：\n{string.Join("\n", types.Select(v => v.Name))}");

            foreach (var handlerProvider in types)
            {
                // get all methods marked with MessageHandlerAttribute , which must be static
                var methods = handlerProvider.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(method => method.GetCustomAttribute<MessageHandlerAttribute>() != null);
                foreach (var method in methods)
                {
                    var attr = method.GetCustomAttribute<MessageHandlerAttribute>();
                    if (attr.type == MessageType.RPC)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length > 1)
                        {
                            // validate parameter , they are must be Session + IRequest + IResponse and return Task
                            var session = parameters[0];
                            if (session.ParameterType != typeof(Session))
                            {
                                Debug.LogError($"{nameof(MessageManager)}: {handlerProvider.Name}.{method.Name} 第一个参数必须是 Session 类型！");
                                continue;
                            }
                            var request = parameters[1];
                            if (!request.ParameterType.IsSubclassOf(typeof(Request)))
                            {
                                Debug.LogError($"{nameof(MessageManager)}: {handlerProvider.Name}.{method.Name} 第二个参数必须是 Request 类型！");
                                continue;
                            }
                            var response = parameters[2];
                            if (!response.ParameterType.IsSubclassOf(typeof(Response)))
                            {
                                Debug.LogError($"{nameof(MessageManager)}: {handlerProvider.Name}.{method.Name} 第三个参数必须是 Response 类型！");
                                continue;
                            }
                            // check response type is match request type
                            var responseType = GetResponseType(request.ParameterType);
                            if (responseType != response.ParameterType)
                            {
                                Debug.LogError($"{nameof(MessageManager)}: {handlerProvider.Name}.{method.Name}  响应类型是{response.Name},但期望值是 {responseType.Name}！");
                                continue;
                            }
                            // check return type is Task
                            if (method.ReturnType != typeof(Task))
                            {
                                Debug.LogError($"{nameof(MessageManager)}: {handlerProvider.Name}.{method.Name}  必须返回 Task 类型！");
                                continue;
                            }
                            // now get the specify handler with request type
                            var handler = RpcMessageHandlers[request.ParameterType];
                            // add this method to handler directly
                            handler.AddTask(method);
                            //LOG 完成了对 xxx 的任务的注册
                            Debug.Log($"{nameof(MessageManager)}: {handlerProvider.Name}.{method.Name} RPC 消息处理器处理逻辑注册成功！");
                        }
                        else
                        {
                            Debug.LogError($"{nameof(MessageManager)}: {handlerProvider.Name}.{method.Name} 至少有 2 个参数！");
                        }
                    }
                    else if (attr.type == MessageType.Normal)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length > 1)
                        {
                            // validate parameter , they are must be Session + IMessage and return void
                            var session = parameters[0];
                            if (session.ParameterType != typeof(Session))
                            {
                                Debug.LogError($"{nameof(MessageManager)}: {handlerProvider.Name}.{method.Name} 第一个参数必须是 Session 类型！");
                                continue;
                            }
                            var message = parameters[1];
                            if (!message.ParameterType.IsSubclassOf(typeof(Message)))
                            {
                                Debug.LogError($"{nameof(MessageManager)}: {handlerProvider.Name}.{method.Name} 第二个参数必须是 Message 类型！");
                                continue;
                            }
                            // check return type is void
                            if (method.ReturnType != typeof(void))
                            {
                                Debug.LogError($"{nameof(MessageManager)}: {handlerProvider.Name}.{method.Name}  必须返回 void 类型！");
                                continue;
                            }
                            // now get the specify handler with request type
                            var handler = NormalMessageHandlers[message.ParameterType];
                            // add this method to handler directly
                            handler.AddTask(method, attr.priority);
                            //LOG 完成了对 xxx 的任务的注册
                            Debug.Log($"{nameof(MessageManager)}: {handlerProvider.Name}.{method.Name} 消息处理器处理逻辑注册成功！");
                        }
                        else
                        {
                            Debug.LogError($"{nameof(MessageManager)}: {handlerProvider.Name}.{method.Name} 至少有 2 个参数！");
                        }
                    }
                }
            }
        }

        private static void RegistPingMessageAndHandlerInternal()
        {
            rpcMessagePairs.Add(typeof(Ping), typeof(Ping));
            var handler = new RpcMessageHandler<Ping, Ping>();
            handler.AddTask(TCPServer.OnPingReceived);
            RpcMessageHandlers.Add(typeof(Ping), handler);
        }

        // 注册所有位于 “com.zframework.tinyrpc.generated” 程序集下的消息处理器
        public static void RegistGeneratedMessageHandlers()
        {
            var types = Assembly.Load("com.zframework.tinyrpc.generated")
                .GetTypes();

            // use reflection to regist rpc message handlers
            var requests = types.Where(type => type.IsSubclassOf(typeof(Request)));
            foreach (var type in requests)
            {
                var handler = Activator.CreateInstance(typeof(RpcMessageHandler<,>)
                    .MakeGenericType(type, GetResponseType(type))) as IRpcMessageHandler;
                try
                {
                    RpcMessageHandlers.Add(type, handler);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"{nameof(MessageManager)}: RPC 消息对 {type.Name} - {GetResponseType(type).Name} 注册失败， {e} ");
                }
            }

            // use reflection to regist normal message handlers
            var messages = types.Where(type => type.IsSubclassOf(typeof(Message)));
            foreach (var type in messages)
            {
                var handler = Activator.CreateInstance(typeof(NormalMessageHandler<>).MakeGenericType(type)) as INormalMessageHandler;
                try
                {
                    NormalMessageHandlers.Add(type, handler);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"{nameof(MessageManager)}: 消息 {type.Name} 注册失败， {e} ");
                }
            }
        }

        public static void RegistRPCMessagePairs()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(v => v.FullName.StartsWith("com.zframework.tinyrpc.generated"));

            if (assembly != null)
            {
                var types = assembly.GetTypes();
                foreach (var type in types)
                {
                    if (type.IsSubclassOf(typeof(Request)))
                    {
                        var attr = type.GetCustomAttribute<ResponseTypeAttribute>();
                        if (attr != null)
                        {
                            try
                            {
                                rpcMessagePairs.Add(type, attr.Type);
                            }
                            catch (Exception e)
                            {
                                Debug.LogWarning($"{nameof(MessageManager)}: RPC 消息对 {type.Name} - {attr.Type} 注册失败， {e} ");
                            }
                        }
                        else
                        {
                            Debug.LogError($"{nameof(MessageManager)}: 请务必为 {type.Name} 通过 ResponseTypeAttribute 配置 Response 消息！");
                        }
                    }
                }
            }
            else
            {
                Debug.LogError($"{nameof(MessageManager)}: 请保证 生成的网络消息在 “com.zframework.tinyrpc.generated” 程序集下");
            }
        }

        internal static void HandleNormalMessage(Session session, IMessage message)
        {
            if (NormalMessageHandlers.TryGetValue(message.GetType(), out var handler))
            {
                handler.Invoke(session, message);
            }
            else
            {
                Debug.LogWarning($"{nameof(MessageManager)}: no handler for message type {message.GetType()}");
            }
        }

        internal static async void HandleRpcRequest(Session session, IRequest request)
        {
            var type = request.GetType();
            IResponse response;
            if (RpcMessageHandlers.TryGetValue(type, out var handler))
            {
                if (rpcMessagePairs.TryGetValue(type, out var responseType))
                {
                    response = Activator.CreateInstance(responseType) as IResponse;
                    response.Id = request.Id;
                    await handler.Invoke(session, request, response);
                }
                else
                {
                    var error = $"RPC 消息 {request.GetType().Name} 没有找到对应的 Response 类型！";
                    response = new Response
                    {
                        Id = request.Id,
                        Error = error
                    };
                    Debug.LogWarning($"{nameof(MessageManager)}: {error}");
                }
            }
            else
            {
                var error = $"RPC 消息 {request.GetType().Name} 没有找到对应的处理器！";
                response = new Response
                {
                    Id = request.Id,
                    Error = error
                };
                Debug.LogWarning($"{nameof(MessageManager)}: {error}");
            }
            session.Reply(response);
        }
        internal static void HandleRpcResponse(Session session, IResponse response)
        {
            if (rpcInfoPairs.TryGetValue(response.Id, out var rpcInfo))
            {
                rpcInfo.task.SetResult(response);
                rpcInfoPairs.Remove(response.Id);
            }
        }

        internal static Task<IResponse> AddRpcTask(IRequest request)
        {
            var tcs = new TaskCompletionSource<IResponse>();
            var cts = new CancellationTokenSource();
            var timeout = Mathf.Max(request.Timeout, 5000); //至少等待 5 秒的响应机会，这在发生复杂操作时很有效
            cts.CancelAfter(timeout);
            var exception = new TimeoutException($"RPC Call Timeout! Request: {request}");
            cts.Token.Register(() => tcs.TrySetException(exception), useSynchronizationContext: false);
            var rpcinfo = new RpcInfo
            {
                id = request.Id,
                task = tcs,
            };
            rpcInfoPairs.Add(request.Id, rpcinfo);
            return tcs.Task;
        }

        // 获取 IRequest 对应的 Response 实例
        public static IResponse CreateResponse([NotNull] IRequest request)
        {
            IResponse response;
            if (!rpcMessagePairs.TryGetValue(request.GetType(), out var type))
            {
                //fallback Response is the base type , thus Response.
                type = typeof(Response);
            }
            response = Activator.CreateInstance(type) as IResponse;
            response.Id = request.Id;
            response.Error = response is Response ? $"RPC 消息 {request.GetType().Name} 没有找到对应的 Response 类型！" : "";

            return response;
        }


        // 获取消息对应的 Response 类型
        public static Type GetResponseType([NotNull] IRequest request)
        {
            if (!rpcMessagePairs.TryGetValue(request.GetType(), out var type))
            {
                throw new Exception($"RPC 消息  Request-Response 为正确完成映射，请参考示例正确注册映射关系！");
            }
            return type;
        }
        public static Type GetResponseType(Type request)
        {
            if (!request.IsSubclassOf(typeof(Request)) && request != typeof(Ping))
            {
                throw new ArgumentException($"指定的参数必须是 Request 的子类！");
            }
            if (!rpcMessagePairs.TryGetValue(request, out var type))
            {
                throw new Exception($"RPC 消息  Request-Response 为正确完成映射，请参考示例正确注册映射关系！");
            }
            return type;
        }
    }
}
