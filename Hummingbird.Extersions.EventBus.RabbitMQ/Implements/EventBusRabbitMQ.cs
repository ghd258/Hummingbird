﻿using Autofac;
using Hummingbird.Extersions.Cache;
using Hummingbird.Extersions.EventBus;
using Hummingbird.Extersions.EventBus.Abstractions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Hummingbird.Extersions.EventBus.RabbitMQ
{
    public struct EventMessage
    {
        public long EventId { get; set; }

        public string MessageId { get; set; }
        public string Body { get; set; }

        public string RouteKey { get; set; }
    }


    /// <summary>
    /// 消息队列
    /// 作者：郭明
    /// 日期：2017年4月5日
    /// </summary>
    public class EventBusRabbitMQ : IEventBus
    {
        private readonly IServiceProvider _lifetimeScope;
        private readonly IRabbitMQPersisterConnectionLoadBalancer _receiveLoadBlancer;
        private readonly IRabbitMQPersisterConnectionLoadBalancer _senderLoadBlancer;        
        private readonly ILogger<IEventBus> _logger;        
        private readonly string _exchange = "amq.topic";
        private readonly string _exchangeType = "topic";
        private readonly ushort _preFetch = 1;
        private readonly int _retryCount = 3;
        private readonly int _IdempotencyDuration;

        private Action<string[], string> _subscribeAckHandler = null;
        private Func<string[], string, Exception, dynamic[], Task<bool>> _subscribeNackHandler = null;
        private static List<IModel> _subscribeChannels = new List<IModel>();
        private readonly IHummingbirdCache<bool> _cacheManager;
        private readonly RetryPolicy _eventBusRetryPolicy = null;

        public EventBusRabbitMQ(
           IHummingbirdCache<bool> cacheManager,
           IRabbitMQPersisterConnectionLoadBalancer receiveLoadBlancer,
           IRabbitMQPersisterConnectionLoadBalancer senderLoadBlancer,
           ILogger<IEventBus> logger,
           IServiceProvider lifetimeScope,
            int retryCount = 3,
            ushort preFetch = 1,
            int IdempotencyDuration = 15,            
            string exchange = "amp.topic",
            string exchangeType = "topic")
        {
            this._receiveLoadBlancer = receiveLoadBlancer;
            this._senderLoadBlancer = senderLoadBlancer;
            this._lifetimeScope = lifetimeScope ?? throw new ArgumentNullException(nameof(lifetimeScope));
            this._IdempotencyDuration = IdempotencyDuration;
            this._cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this._retryCount = retryCount;
            this._preFetch = preFetch;
            this._exchange = exchange;
            this._exchangeType = exchangeType;
            this._eventBusRetryPolicy = RetryPolicy.Handle<BrokerUnreachableException>()
           .Or<SocketException>()
           .Or<System.IO.IOException>()
           .Or<AlreadyClosedException>()
           .WaitAndRetry(_retryCount, retryAttempt => TimeSpan.FromMilliseconds(Math.Pow(2, retryAttempt)), (ex, time) =>
           {
               _logger.LogWarning(ex.ToString());
           });
        }

        /// <summary>
        /// 发送消息
        /// </summary>
        public async Task PublishNonConfirmAsync(List<Models.EventLogEntry> Events,
            int EventDelaySeconds = 0)
        {

            var evtDicts = Events.Select(a => new EventMessage()
            {
                Body = a.Content,
                MessageId = a.MessageId.ToString(),
                EventId = a.EventId,
                RouteKey = a.EventTypeName
            }).ToList();

            await EnqueueNoConfirm(evtDicts,EventDelaySeconds);
        }

        /// <summary>
        /// 发送消息
        /// </summary>
        public async Task PublishAsync(
            List<Models.EventLogEntry> Events,
            Action<List<Models.EventLogEntry>> ackHandler = null,
            Action<List<Models.EventLogEntry>> nackHandler = null,
            Action<List<Models.EventLogEntry>> returnHandler = null,
            int EventDelaySeconds = 0,
            int TimeoutMilliseconds = 500,
            int BatchSize = 500)
        {
            var evtDicts = Events.Select(a => new EventMessage()
            {
                Body = a.Content,
                MessageId = a.MessageId.ToString(),
                EventId = a.EventId,
                RouteKey = a.EventTypeName
            }).ToList();

            await EnqueueConfirm(evtDicts,(messageIds)=> {

                ackHandler(Events.Where(@event => messageIds.Contains(@event.MessageId)).ToList());                

            }, (messageIds)=> {

                nackHandler(Events.Where(@event => messageIds.Contains(@event.MessageId)).ToList());

            }, (messageIds)=> {
                returnHandler(Events.Where(@event => messageIds.Contains(@event.MessageId)).ToList());
            }, EventDelaySeconds, TimeoutMilliseconds, BatchSize);
        }

        /// <summary>
        /// 发送消息
        /// </summary>
        public async Task<bool> PublishAsync(
            List<Models.EventLogEntry> Events,
            int EventDelaySeconds = 0,
            int TimeoutMilliseconds = 500,
            int BatchSize = 500)
        {
            var evtDicts = Events.Select(a => new EventMessage()
            {
                Body = a.Content,
                MessageId = a.MessageId.ToString(),
                EventId = a.EventId,
                RouteKey = a.EventTypeName
            }).ToList();

            bool result = true;

            await EnqueueConfirm(evtDicts, (messageIds) => {

            }, (messageIds) => {

                result = false;

            }, (messageIds) => {
                result = false;
            }, EventDelaySeconds, TimeoutMilliseconds, BatchSize);

            return result;
        }

        async Task EnqueueNoConfirm(
            List<EventMessage> Events,
          int EventDelaySeconds)
        {
            var persistentConnection = await _senderLoadBlancer.Lease();

            try
            {
                if (!persistentConnection.IsConnected)
                {
                    persistentConnection.TryConnect();
                }

                using (var _channel = persistentConnection.CreateModel())
                {
                    // 提交走批量通道
                    var _batchPublish = _channel.CreateBasicPublishBatch();
                    
                    for (var eventIndex = 0; eventIndex < Events.Count; eventIndex++)
                    {
                    
                            var MessageId = Events[eventIndex].MessageId;
                            var EventId = Events[eventIndex].EventId;

                            var json = Events[eventIndex].Body;
                            var routeKey = Events[eventIndex].RouteKey;
                            byte[] bytes = Encoding.UTF8.GetBytes(json);
                            //设置消息持久化
                            IBasicProperties properties = _channel.CreateBasicProperties();
                            properties.DeliveryMode = 2;
                            properties.MessageId = MessageId;
                            properties.Headers = new Dictionary<string, Object>();
                            properties.Headers["EventId"] = EventId;
                        

                            //需要发送延时消息
                        if (EventDelaySeconds > 0)
                            {
                                var newQueue = routeKey + ".DELAY." + EventDelaySeconds;

                                Dictionary<string, object> dic = new Dictionary<string, object>();
                                dic.Add("x-expires", EventDelaySeconds * 10000);//队列过期时间 
                                dic.Add("x-message-ttl", EventDelaySeconds * 1000);//当一个消息被推送在该队列的时候 可以存在的时间 单位为ms，应小于队列过期时间  
                                dic.Add("x-dead-letter-exchange", _exchange);//过期消息转向路由  
                                dic.Add("x-dead-letter-routing-key", routeKey);//过期消息转向路由相匹配routingkey 

                                //创建一个队列                         
                                _channel.QueueDeclare(
                                        queue: newQueue,
                                        durable: true,
                                        exclusive: false,
                                        autoDelete: false,
                                        arguments: dic);

                                //发送至延时队列，延时结束后会写入正式度列
                                _batchPublish.Add(
                                    exchange: "",
                                    mandatory: true,
                                    routingKey: newQueue,
                                    properties: properties,
                                    body: bytes);

                            }
                            else
                            {
                                //发送到正常队列，如果Reject会写入死信队列
                                _batchPublish.Add(
                                    exchange: _exchange,
                                    mandatory: true,
                                    routingKey: routeKey,
                                    properties: properties,
                                    body: bytes);
                            }                   
                    };

                    await _eventBusRetryPolicy.Execute(async () =>
                    {
                        await Task.Run(() =>
                        {
                            //批量提交
                            _batchPublish.Publish();
                        });
                    });
                }


            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);

            }
        }

        async Task EnqueueConfirm(
          List<EventMessage> Events,
          Action<List<string>> ackHandler,
          Action<List<string>> nackHandler,
          Action<List<string>> returnHandler,
          int EventDelaySeconds,
          int TimeoutMilliseconds,
          int BatchSize)
        {
            var persistentConnection = await _senderLoadBlancer.Lease();
            
            try
            {
                if (!persistentConnection.IsConnected)
                {
                    persistentConnection.TryConnect();
                }
                //消息发送成功后回调后需要修改数据库状态，改成本地做组缓存后，再批量入库。（性能提升百倍）
                var _batchBlock_BasicReturn = new BatchBlock<string>(BatchSize);
                var _batchBlock_BasicAcks = new BatchBlock<string>(BatchSize);
                var _batchBlock_BasicNacks = new BatchBlock<string>(BatchSize);
                var _actionBlock_BasicReturn = new ActionBlock<string[]>(MessageIds =>
                {
                    if (returnHandler != null && MessageIds.Length > 0)
                    {
                        returnHandler(MessageIds.ToList());
                    }
                }, new ExecutionDataflowBlockOptions()
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount
                });

                var _actionBlock_BasicAcks = new ActionBlock<string[]>(MessageIds =>
                {
                    if (ackHandler != null && MessageIds.Length > 0)
                    {
                        ackHandler(MessageIds.ToList());
                    }
                }, new ExecutionDataflowBlockOptions()
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                });

                var _actionBlock_BasicNacks = new ActionBlock<string[]>(MessageIds =>
                {
                    if (nackHandler != null && MessageIds.Length > 0)
                    {
                        nackHandler(MessageIds.ToList());
                    }
                }, new ExecutionDataflowBlockOptions()
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount
                });

                _batchBlock_BasicReturn.LinkTo(_actionBlock_BasicReturn);
                _batchBlock_BasicAcks.LinkTo(_actionBlock_BasicAcks);
                _batchBlock_BasicNacks.LinkTo(_actionBlock_BasicNacks);

                using (var _channel = persistentConnection.CreateModel())
                {
                    //保存EventId和DeliveryTag 映射
                    var unconfirmMessageIds = new string[Events.Count];
                    var returnMessageIds = new Dictionary<string, bool>();
                    ulong lastDeliveryTag = 0;

                    //消息无法投递失被退回（如：队列找不到）
                    _channel.BasicReturn += async (object sender, BasicReturnEventArgs e) =>
                    {
                        if (!string.IsNullOrEmpty(e.BasicProperties.MessageId))
                        {
                            var MessageId = e.BasicProperties.MessageId;

                            if (!string.IsNullOrEmpty(MessageId))
                            {
                                if (!returnMessageIds.ContainsKey(MessageId))
                                {
                                    returnMessageIds.Add(MessageId, false);
                                }

                                await _batchBlock_BasicReturn.SendAsync(MessageId);
                            }
                        }
                    };

                    //消息路由到队列并持久化后执行
                    _channel.BasicAcks += async (object sender, BasicAckEventArgs e) =>
                    {
                        if (e.Multiple)
                        {
                            for (var i = lastDeliveryTag; i < e.DeliveryTag; i++)
                            {
                                var messageId = unconfirmMessageIds[i];
                                if (!string.IsNullOrEmpty(messageId))
                                {
                                    unconfirmMessageIds[i] = "";

                                    if (returnMessageIds.Count > 0)
                                    {
                                        if (!returnMessageIds.ContainsKey(messageId))
                                        {
                                            await _batchBlock_BasicAcks.SendAsync(messageId);
                                        }
                                    }
                                    else
                                    {
                                        await _batchBlock_BasicAcks.SendAsync(messageId);
                                    }
                                }
                            }

                            // 批量回调，记录当期位置
                            lastDeliveryTag = e.DeliveryTag;
                        }
                        else
                        {
                            var messageId = unconfirmMessageIds[e.DeliveryTag - 1];

                            if (!string.IsNullOrEmpty(messageId))
                            {
                                unconfirmMessageIds[e.DeliveryTag - 1] = "";

                                if (returnMessageIds.Count > 0)
                                {
                                    if (!returnMessageIds.ContainsKey(messageId))
                                    {
                                        await _batchBlock_BasicAcks.SendAsync(messageId);
                                    }
                                }
                                else
                                {
                                    await _batchBlock_BasicAcks.SendAsync(messageId);
                                }
                            }
                        }
                    };

                    //消息投递失败
                    _channel.BasicNacks += async (object sender, BasicNackEventArgs e) =>
                    {

                        if (e.Multiple)
                        {
                            for (var i = lastDeliveryTag; i < e.DeliveryTag; i++)
                            {
                                var messageId = unconfirmMessageIds[i];
                                if (!string.IsNullOrEmpty(messageId))
                                {
                                    unconfirmMessageIds[i] ="";

                                    if (returnMessageIds.Count > 0)
                                    {
                                        if (!returnMessageIds.ContainsKey(messageId))
                                        {
                                            await _batchBlock_BasicNacks.SendAsync(messageId);
                                        }
                                    }
                                    else
                                    {
                                        await _batchBlock_BasicNacks.SendAsync(messageId);
                                    }
                                }
                            }

                            // 批量回调，记录当期位置
                            lastDeliveryTag = e.DeliveryTag;
                        }
                        else
                        {
                            var messageId = unconfirmMessageIds[e.DeliveryTag - 1];
                            if (!string.IsNullOrEmpty(messageId))
                            {
                                unconfirmMessageIds[e.DeliveryTag - 1] = "";

                                if (returnMessageIds.Count > 0)
                                {
                                    if (!returnMessageIds.ContainsKey(messageId))
                                    {
                                        await _batchBlock_BasicNacks.SendAsync(messageId);
                                    }
                                }
                                else
                                {
                                    await _batchBlock_BasicNacks.SendAsync(messageId);
                                }
                            }

                        }
                    };

                    _eventBusRetryPolicy.Execute(() =>
                    {
                        _channel.ConfirmSelect();
                    });

                    // 提交走批量通道
                    var _batchPublish = _channel.CreateBasicPublishBatch();
                    

                    for (var eventIndex = 0; eventIndex < Events.Count; eventIndex++)
                    {
                        _eventBusRetryPolicy.Execute(() =>
                        {
                            var MessageId = Events[eventIndex].MessageId;
                            var EventId = Events[eventIndex].EventId;

                            var json = Events[eventIndex].Body;
                            var routeKey = Events[eventIndex].RouteKey;
                            byte[] bytes = Encoding.UTF8.GetBytes(json);
                            //设置消息持久化
                            IBasicProperties properties = _channel.CreateBasicProperties();
                            properties.DeliveryMode = 2;
                            properties.MessageId = MessageId;                            
                            properties.Headers = new Dictionary<string, Object>();                                                         
                            unconfirmMessageIds[eventIndex] = MessageId;

                            //需要发送延时消息
                            if (EventDelaySeconds > 0)
                            {
                                var newQueue = routeKey + ".DELAY." + EventDelaySeconds;

                                Dictionary<string, object> dic = new Dictionary<string, object>();
                                dic.Add("x-expires", EventDelaySeconds * 10000);//队列过期时间 
                                dic.Add("x-message-ttl", EventDelaySeconds * 1000);//当一个消息被推送在该队列的时候 可以存在的时间 单位为ms，应小于队列过期时间  
                                dic.Add("x-dead-letter-exchange", _exchange);//过期消息转向路由  
                                dic.Add("x-dead-letter-routing-key", routeKey);//过期消息转向路由相匹配routingkey 

                                //创建一个队列                         
                                _channel.QueueDeclare(
                                        queue: newQueue,
                                        durable: true,
                                        exclusive: false,
                                        autoDelete: false,
                                        arguments: dic);

                                //发送至延时队列，延时结束后会写入正式度列
                                _batchPublish.Add(
                                    exchange: "",
                                    mandatory: true,
                                    routingKey: newQueue,
                                    properties: properties,
                                    body: bytes);

                            }
                            else
                            {
                                //发送到正常队列，如果Reject会写入死信队列
                                _batchPublish.Add(
                                    exchange: _exchange,
                                    mandatory: true,
                                    routingKey: routeKey,
                                    properties: properties,
                                    body: bytes);
                            }
                            
                        });
                    };

                    await _eventBusRetryPolicy.Execute(async () =>
                    {
                        await Task.Run(() =>
                        {
                            //批量提交
                            _batchPublish.Publish();
                            
                            _channel.WaitForConfirms(TimeSpan.FromMilliseconds(TimeoutMilliseconds));
                        });
                    });
                }

                _batchBlock_BasicAcks.Complete();
                _batchBlock_BasicNacks.Complete();
                _batchBlock_BasicReturn.Complete();

                await _batchBlock_BasicReturn.Completion.ContinueWith(delegate { _actionBlock_BasicReturn.Complete(); _actionBlock_BasicReturn.Completion.Wait(); });
                await _batchBlock_BasicAcks.Completion.ContinueWith(delegate { _actionBlock_BasicAcks.Complete(); _actionBlock_BasicAcks.Completion.Wait(); });
                await _batchBlock_BasicNacks.Completion.ContinueWith(delegate { _actionBlock_BasicNacks.Complete(); _actionBlock_BasicNacks.Completion.Wait(); });

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);

            }
        }

        /// <summary>
        /// 订阅消息（同一类消息可以重复订阅）
        /// 作者：郭明
        /// 日期：2017年4月3日
        /// </summary>
        /// <typeparam name="TD"></typeparam>
        /// <typeparam name="TH"></typeparam>
        /// <param name="QueueName">消息类型名称</param>        
        /// <param name="EventTypeName">消息类型名称</param>        
        /// <returns></returns>
        public IEventBus Register<TD, TH>(string QueueName, string EventTypeName = "")
                where TD : class
                where TH : IEventHandler<TD>
        {
            var persistentConnection = _receiveLoadBlancer.Lease().Result;

            if (!persistentConnection.IsConnected)
            {
                persistentConnection.TryConnect();
            }
           
            var _channel = persistentConnection.CreateModel();
            var policy = createPolicy();
            var msgHandlerPolicy = Policy<Boolean>.Handle<Exception>().FallbackAsync(false)
                .WrapAsync(policy);

            var _queueName = string.IsNullOrEmpty(QueueName)? typeof(TH).FullName: QueueName;
            var _routeKey = string.IsNullOrEmpty(EventTypeName) ? typeof(TD).FullName : EventTypeName;
            var EventAction = _lifetimeScope.GetService(typeof(TH)) as IEventHandler<TD>;
          
            if (EventAction == null)
            {
                
                EventAction = System.Activator.CreateInstance(typeof(TH)) as IEventHandler<TD>;
            }

            //direct fanout topic  
            _channel.ExchangeDeclare(_exchange, _exchangeType, true, false, null);

            //在MQ上定义一个持久化队列，如果名称相同不会重复创建
            _channel.QueueDeclare(_queueName, true, false, false, null);
            //绑定交换器和队列
            _channel.QueueBind(_queueName, _exchange, _routeKey);
            //输入1，那如果接收一个消息，但是没有应答，则客户端不会收到下一个消息
            _channel.BasicQos(0, _preFetch, false);
            //在队列上定义一个消费者a
            EventingBasicConsumer consumer = new EventingBasicConsumer(_channel);
            
            consumer.Received += async (ch, ea) =>
            {
                try
                {
                    if (!persistentConnection.IsConnected)
                    {
                        persistentConnection.TryConnect();
                    }
                    byte[] bytes;
                    string str = string.Empty;
                    var msg = default(TD);

                    var MessageId = ea.BasicProperties.MessageId;

                    if (!string.IsNullOrEmpty(MessageId) && (_IdempotencyDuration == 0 || !_cacheManager.Exists($"{_queueName}:{MessageId}", "Events")))
                    {

                        try
                        {
                            bytes = ea.Body;
                            str = Encoding.UTF8.GetString(bytes);
                            msg = JsonConvert.DeserializeObject<TD>(str);

                            var handlerOK = await msgHandlerPolicy.ExecuteAsync(async (cancellationToken) =>
                            {
                                return await EventAction.Handle(msg, cancellationToken);

                            }, CancellationToken.None);

                            if (handlerOK)
                            {
                                if (_subscribeAckHandler != null)
                                {
                                    _subscribeAckHandler(new string[] { MessageId }, _queueName);
                                }
                                //确认消息
                                _channel.BasicAck(ea.DeliveryTag, false);

                                //幂等保证
                                if (_IdempotencyDuration > 0)
                                {
                                    _cacheManager.Add($"{_queueName}:{MessageId}", true, TimeSpan.FromSeconds(_IdempotencyDuration), "Events");
                                }
                            }
                            else
                            {
                                //重新入队，默认：是
                                var requeue = true;

                                //执行回调，等待业务层确认是否重新入队
                                if (_subscribeNackHandler != null)
                                {
                                    requeue = await _subscribeNackHandler(new string[] { MessageId }, _queueName, null, new dynamic[] { msg });
                                }

                                //确认消息
                                _channel.BasicReject(ea.DeliveryTag, requeue);

                            }
                        }
                        catch (Exception ex)
                        {
                            //重新入队，默认：是
                            var requeue = true;

                            //执行回调，等待业务层的处理结果
                            if (_subscribeNackHandler != null)
                            {
                                requeue = await _subscribeNackHandler(new string[] { MessageId }, _queueName, ex, new dynamic[] { msg });

                            }

                            //确认消息
                            _channel.BasicReject(ea.DeliveryTag, requeue);
                        }
                    }
                    else
                    {
                        //确认处理（消息被丢弃）
                        _channel.BasicAck(ea.DeliveryTag, false);
                    }
                }
                catch(Exception ex)
                {
                    _logger.LogError(ex.Message, ex);
                }
            };

            consumer.Unregistered += (ch, ea) =>
            {
                _logger.LogDebug($"MQ:{_queueName} Consumer_Unregistered");
            };

            consumer.Registered += (ch, ea) =>
            {
                _logger.LogDebug($"MQ:{_queueName} Consumer_Registered");
            };

            consumer.Shutdown += (ch, ea) =>
            {
                _logger.LogDebug($"MQ:{_queueName} Consumer_Shutdown.{ea.ReplyText}");
            };

            consumer.ConsumerCancelled += (object sender, ConsumerEventArgs e) =>
            {
                _logger.LogDebug($"MQ:{_queueName} ConsumerCancelled");
            };

            //消费队列，并设置应答模式为程序主动应答
            _channel.BasicConsume(_queueName, false, consumer);

            _subscribeChannels.Add(_channel);
            return this;
        }

        private IAsyncPolicy createPolicy() {

            IAsyncPolicy policy = Policy.NoOpAsync();//创建一个空的Policy

            //设置熔断策略
            policy = policy.WrapAsync(Policy.Handle<Exception>()
                .AdvancedCircuitBreakerAsync(
                    failureThreshold: 0.5, // Break on >=50% actions result in handled exceptions...
                    samplingDuration: TimeSpan.FromSeconds(10), // ... over any 10 second period
                    minimumThroughput: 8, // ... provided at least 8 actions in the 10 second period.
                    durationOfBreak: TimeSpan.FromSeconds(30), // Break for 30 seconds.
                    onBreak: (Exception exception, TimeSpan timeSpan) =>
                    {
                        Console.WriteLine("onBreak!");
                    },
                    onHalfOpen: () =>
                    {
                        Console.WriteLine("onReset!");
                    },
                    onReset: () =>
                    {
                        Console.WriteLine("onReset!");
                    }));

            //设置重试策略
            policy = policy.WrapAsync(Policy.Handle<Exception>()
                   .RetryAsync(3, (ex, time) =>
                   {
                       _logger.LogError(ex, ex.ToString());
                   }));


            // 设置超时
            policy = policy.WrapAsync(Policy.TimeoutAsync(
                TimeSpan.FromSeconds(2),
                TimeoutStrategy.Pessimistic,
                (context, timespan, task) =>
                {
                    Console.WriteLine("Timeout!");

                    return Task.FromResult(true);
                }));

     

            return policy;

        }

        /// <summary>
        /// 订阅消息（同一类消息可以重复订阅）
        /// 作者：郭明
        /// 日期：2017年4月3日
        /// </summary>
        /// <typeparam name="TD"></typeparam>
        /// <typeparam name="TH"></typeparam>
        /// <param name="EventTypeName">消息类型名称</param>        
        /// <returns></returns>
        public IEventBus RegisterBatch<TD, TH>(string QueueName, string EventTypeName = "", int BatchSize =50)
                where TD : class
                where TH : IEventBatchHandler<TD>
        {
            var persistentConnection = _receiveLoadBlancer.Lease().Result;

            if (!persistentConnection.IsConnected)
            {
                persistentConnection.TryConnect();
            }

            var _channel = persistentConnection.CreateModel();
            var policy = createPolicy();
            var msgHandlerPolicy = Policy<Boolean>.Handle<Exception>().FallbackAsync(false)
                .WrapAsync(policy);
            var _queueName = string.IsNullOrEmpty(QueueName) ? typeof(TH).FullName : QueueName;
            var _routeKey = string.IsNullOrEmpty(EventTypeName) ? typeof(TD).FullName : EventTypeName;
            var EventAction = _lifetimeScope.GetService(typeof(TH)) as IEventBatchHandler<TD>;

            if (EventAction == null)
            {
                EventAction = System.Activator.CreateInstance(typeof(TH)) as IEventBatchHandler<TD>;
            }

            //direct fanout topic  
            _channel.ExchangeDeclare(_exchange, _exchangeType, true, false, null);

            //在MQ上定义一个持久化队列，如果名称相同不会重复创建
            _channel.QueueDeclare(_queueName, true, false, false, null);
            //绑定交换器和队列
            _channel.QueueBind(_queueName, _exchange, _routeKey);
            //输入1，那如果接收一个消息，但是没有应答，则客户端不会收到下一个消息
            _channel.BasicQos(0,(ushort)BatchSize, false);

            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        var batchPool = new ConcurrentDictionary<string, TD>();
                        ulong batchLastDeliveryTag = 0;
                        var _insertPoolBlock = new ActionBlock<BasicGetResult>(ea =>
                        {
                            //消息编号不为空, 消息没有被处理过则写入待处理列表
                            if (!string.IsNullOrEmpty(ea.BasicProperties.MessageId) && (_IdempotencyDuration == 0 || !_cacheManager.Exists($"{_queueName}:{ea.BasicProperties.MessageId}", "Events")))
                            {

                                batchPool.TryAdd(ea.BasicProperties.MessageId, JsonConvert.DeserializeObject<TD>(Encoding.UTF8.GetString(ea.Body)));
                                batchLastDeliveryTag = ea.DeliveryTag;
                                return;

                            }

                            //丢弃，消息已经被处理或者没有消息编号
                            _channel.BasicNack(ea.DeliveryTag, false, false);
                        });

                        #region batch Pull
                        for (var i = 0; i < BatchSize; i++)
                        {
                            
                            var ea = _channel.BasicGet(_queueName, false);
                            if (ea != null)
                            {
                                _insertPoolBlock.Post(ea);
                            }
                            else
                            {
                                break;
                            }
                        }

                        #endregion

                        //等待消息写入度列完成
                        _insertPoolBlock.Complete();
                        await _insertPoolBlock.Completion;

                        //队列不为空
                        if (batchPool.Count > 0)
                        {
                            var eventIds = batchPool.Select(a => a.Key).ToArray();
                            var bodys = batchPool.Select(a => a.Value).ToArray();

                            try
                            {
                                var handlerOK = await msgHandlerPolicy.ExecuteAsync(async (cancellationToken) =>
                                 {
                                     return await EventAction.Handle(bodys, cancellationToken);

                                 }, CancellationToken.None);

                                if (handlerOK)
                                {
                                    #region 消息处理成功
                                    if (_subscribeAckHandler != null)
                                    {
                                        _subscribeAckHandler(eventIds, _queueName);
                                    }

                                    //确认消息被处理
                                    _channel.BasicAck(batchLastDeliveryTag, true);

                                    //消息幂等
                                    if (_IdempotencyDuration > 0)
                                    {
                                        for (int i = 0; i < eventIds.Length; i++)
                                        {
                                            _cacheManager.Add($"{_queueName}:{eventIds[i].ToString()}", true, TimeSpan.FromSeconds(_IdempotencyDuration), "Events");
                                        }
                                    }

                                    #endregion
                                }
                                else
                                {
                                    #region 消息处理失败
                                    var requeue = true;

                                    if (_subscribeNackHandler != null)
                                    {
                                        requeue = await _subscribeNackHandler(eventIds, _queueName, null, bodys);
                                    }

                                    _channel.BasicNack(batchLastDeliveryTag, true, requeue);

                                    #endregion
                                }
                            }
                            catch (Exception ex)
                            {
                                #region 业务处理消息出现异常，消息重新写入队列，超过最大重试次数后不再写入队列
                                var requeue = true;

                                if (_subscribeNackHandler != null)
                                {
                                    requeue = await _subscribeNackHandler(eventIds, _queueName, ex, bodys);
                                }
                                _channel.BasicNack(batchLastDeliveryTag, true, requeue);

                                #endregion
                            }
                        }
                    }
                    catch(Exception ex)
                    {
                        _logger.LogError(ex.Message, ex);
                    }

                }
            });
            _subscribeChannels.Add(_channel);
            return this;
        }

        public IEventBus Subscribe(
            Action<string[], string> ackHandler,
            Func<string[], string, Exception, dynamic[], Task<bool>> nackHandler)
        {
            _subscribeAckHandler = ackHandler;
            _subscribeNackHandler = nackHandler;
            return this;
        }
    }
}
