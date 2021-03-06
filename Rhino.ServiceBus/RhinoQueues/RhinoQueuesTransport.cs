using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Transactions;
using System.Xml;
using log4net;
using Microsoft.Isam.Esent.Interop;
using Rhino.Queues;
using Rhino.Queues.Model;
using Rhino.ServiceBus.Impl;
using Rhino.ServiceBus.Internal;
using Rhino.ServiceBus.Messages;
using Rhino.ServiceBus.Transport;
using Rhino.ServiceBus.Util;
using Transaction = System.Transactions.Transaction;

namespace Rhino.ServiceBus.RhinoQueues
{
	public class RhinoQueuesTransport : ITransport
	{
		private readonly Uri endpoint;
		private readonly IEndpointRouter endpointRouter;
		private readonly IMessageSerializer messageSerializer;
		private readonly int threadCount;
		private readonly string path;
		private QueueManager queueManager;
		private readonly Thread[] threads;
		private readonly string queueName;
		private volatile bool shouldContinue;
		private bool haveStarted;
		private readonly IsolationLevel queueIsolationLevel;
		private readonly int numberOfRetries;

		[ThreadStatic]
		private static RhinoQueueCurrentMessageInformation currentMessageInformation;

		private readonly ILog logger = LogManager.GetLogger(typeof(RhinoQueuesTransport));
		private TimeoutAction timeout;
		private IQueue queue;


		public RhinoQueuesTransport(
			Uri endpoint,
			IEndpointRouter endpointRouter,
			IMessageSerializer messageSerializer,
			int threadCount,
			string path,
			IsolationLevel queueIsolationLevel,
			int numberOfRetries)
		{
			this.endpoint = endpoint;
			this.queueIsolationLevel = queueIsolationLevel;
			this.numberOfRetries = numberOfRetries;
			this.endpointRouter = endpointRouter;
			this.messageSerializer = messageSerializer;
			this.threadCount = threadCount;
			this.path = path;

			queueName = endpoint.GetQueueName();

			threads = new Thread[threadCount];

			// This has to be the first subscriber to the transport events
			// in order to successfuly handle the errors semantics
			new ErrorAction(numberOfRetries).Init(this);
		}

		public void Dispose()
		{
			shouldContinue = false;
			logger.DebugFormat("Stopping transport for {0}", endpoint);

			if (timeout != null)
				timeout.Dispose();
			DisposeQueueManager();

			if (!haveStarted)
				return;

			foreach (var thread in threads)
			{
				thread.Join();
			}
		}

		private void DisposeQueueManager()
		{
			if (queueManager != null)
			{
				const int retries = 5;
				int tries = 0;
				bool disposeRudely = false;
				while (true)
				{
					try
					{
						queueManager.Dispose();
						break;
					}
					catch (EsentErrorException e)
					{
						tries += 1;
						if (tries > retries)
						{
							disposeRudely = true;
							break;
						}
						if (e.Error != JET_err.TooManyActiveUsers)
							throw;
						// let the other threads a chance to complete their work
						Thread.Sleep(50);
					}
				}
				if (disposeRudely)
					queueManager.DisposeRudely();
			}
		}

		[CLSCompliant(false)]
		public IQueue Queue
		{
			get { return queue; }
		}

		public void Start()
		{
			shouldContinue = true;

			var port = endpoint.Port;
			if (port == -1)
				port = 2200;
			queueManager = new QueueManager(new IPEndPoint(IPAddress.Any, port), path);
			queueManager.CreateQueues(queueName);

			queue = queueManager.GetQueue(queueName);

			timeout = new TimeoutAction(queue);
			logger.DebugFormat("Starting {0} threads to handle messages on {1}, number of retries: {2}",
				threadCount, endpoint, numberOfRetries);
			for (var i = 0; i < threadCount; i++)
			{
				threads[i] = new Thread(ReceiveMessage)
				{
					Name = "Rhino Service Bus Worker Thread #" + i,
					IsBackground = true
				};
				threads[i].Start(i);
			}
			haveStarted = true;
			var started = Started;
			if (started != null)
				started();
		}

		private void ReceiveMessage(object context)
		{
			while (shouldContinue)
			{
				try
				{
					queueManager.Peek(queueName);
				}
				catch (TimeoutException)
				{
					logger.DebugFormat("Could not find a message on {0} during the timeout period",
									   endpoint);
					continue;
				}
				catch (ObjectDisposedException)
				{
					logger.DebugFormat("Shutting down the transport for {0} thread {1}", endpoint, context);
					return;
				}
				catch (EsentErrorException e)
				{
					// we were shut down, so just return
					if (e.Error == JET_err.TermInProgress)
					{
						logger.DebugFormat("Shutting down the transport for {0} thread {1}", endpoint, context);
					}
					else
					{
						logger.Error("An error occured while recieving a message, shutting down message processing thread", e);
					}
					return;
				}

				if (shouldContinue == false)
					return;

				var transactionOptions = GetTransactionOptions();
				using (var tx = new TransactionScope(TransactionScopeOption.Required, transactionOptions))
				{
					Message message;
					try
					{
						message = queueManager.Receive(queueName, TimeSpan.FromSeconds(1));
					}
					catch (TimeoutException)
					{
						logger.DebugFormat("Could not find a message on {0} during the timeout period",
									   endpoint);
						continue;
					}
					catch (EsentErrorException e)
					{
						// we were shut down, so just return
						if (e.Error == JET_err.TermInProgress)
							return;
						logger.Error("An error occured while recieving a message, shutting down message processing thread", e);
						return;
					}
					catch (Exception e)
					{
						logger.Error("An error occured while recieving a message, shutting down message processing thread", e);
						return;
					}

					try
					{
						var msgType = (MessageType)Enum.Parse(typeof(MessageType), message.Headers["type"]);
						logger.DebugFormat("Starting to handle message {0} of type {1} on {2}",
									   message.Id,
									   msgType,
									   endpoint);
						switch (msgType)
						{
							case MessageType.AdministrativeMessageMarker:
								ProcessMessage(message, tx,
									   AdministrativeMessageArrived,
									   AdministrativeMessageProcessingCompleted,
									   null);
								break;
							case MessageType.ShutDownMessageMarker:
								//ignoring this one
								tx.Complete();
								break;
							case MessageType.TimeoutMessageMarker:
								var timeToSend = XmlConvert.ToDateTime(message.Headers["time-to-send"], XmlDateTimeSerializationMode.Utc);
								if (timeToSend > DateTime.Now)
								{
									timeout.Register(message);
									queue.MoveTo(SubQueue.Timeout.ToString(), message);
									tx.Complete();
								}
								else
								{
									ProcessMessage(message, tx,
												   MessageArrived,
												   MessageProcessingCompleted,
												   null);
								}
								break;
							default:
								ProcessMessage(message, tx,
									   MessageArrived,
									   MessageProcessingCompleted,
									   BeforeMessageTransactionCommit);
								break;
						}
					}
					catch (Exception exception)
					{
						logger.Debug("Could not process message", exception);
					}
				}

			}
		}

		private void ProcessMessage(
			Message message,
			TransactionScope tx,
			Func<CurrentMessageInformation, bool> messageRecieved,
			Action<CurrentMessageInformation, Exception> messageCompleted,
			Action<CurrentMessageInformation> beforeTransactionCommit)
		{
			Exception ex = null;
			try
			{
				//deserialization errors do not count for module events
				object[] messages = DeserializeMessages(message);
				try
				{
					var messageId = new Guid(message.Headers["id"]);
					var source = new Uri(message.Headers["source"]);
					foreach (var msg in messages)
					{
						currentMessageInformation = new RhinoQueueCurrentMessageInformation
						{
							AllMessages = messages,
							Message = msg,
							Destination = endpoint,
							MessageId = messageId,
							Source = source,
							TransportMessageId = message.Id.ToString(),
							Queue = queue,
							TransportMessage = message
						};

						if (TransportUtil.ProcessSingleMessage(currentMessageInformation, messageRecieved) == false)
							Discard(currentMessageInformation.Message);
					}
				}
				catch (Exception e)
				{
					ex = e;
					logger.Error("Failed to process message", e);
				}
			}
			catch (Exception e)
			{
				ex = e;
				logger.Error("Failed to deserialize message", e);
			}
			finally
			{
				var messageHandlingCompletion = new MessageHandlingCompletion(tx, null, ex, messageCompleted, beforeTransactionCommit, logger,
				                                                              MessageProcessingFailure, currentMessageInformation);
				messageHandlingCompletion.HandleMessageCompletion();
				currentMessageInformation = null;
			}
		}

		private void Discard(object message)
		{
			logger.DebugFormat("Discarding message {0} ({1}) because there are no consumers for it.",
				message, currentMessageInformation.TransportMessageId);
			Send(new Endpoint { Uri = endpoint.AddSubQueue(SubQueue.Discarded) }, new[] { message });
		}

		private object[] DeserializeMessages(Message message)
		{
			try
			{
				return messageSerializer.Deserialize(new MemoryStream(message.Data));
			}
			catch (Exception e)
			{
				try
				{
					logger.Error("Error when serializing message", e);
					var serializationError = MessageSerializationException;
					if (serializationError != null)
					{
						var information = new RhinoQueueCurrentMessageInformation
						{
							Message = message,
							Source = new Uri(message.Headers["from"]),
							MessageId = new Guid(message.Headers["id"])
						};
						serializationError(information, e);
					}
				}
				catch (Exception moduleEx)
				{
					logger.Error("Error when notifying about serialization exception", moduleEx);
				}
				throw;
			}
		}

		public Endpoint Endpoint
		{
			get { return endpointRouter.GetRoutedEndpoint(endpoint); }
		}

		public int ThreadCount
		{
			get { return threadCount; }
		}

		public CurrentMessageInformation CurrentMessageInformation
		{
			get { return currentMessageInformation; }
		}

		public void Send(Endpoint destination, object[] msgs)
		{
			SendInternal(msgs, destination, nv => { });
		}

		private void SendInternal(object[] msgs, Endpoint destination, Action<NameValueCollection> customizeHeaders)
		{
			var messageId = Guid.NewGuid();
			using (var memoryStream = new MemoryStream())
			{
				messageSerializer.Serialize(msgs, memoryStream);

				var payload = new MessagePayload
				{
					Data = memoryStream.ToArray(),
					Headers =
                        {
                            {"id", messageId.ToString()},
                            {"type", GetAppSpecificMarker(msgs).ToString()},
                            {"source", Endpoint.Uri.ToString()},
                        }
				};
				logger.DebugFormat("Sending a message with id '{0}' to '{1}'", messageId, destination.Uri);
				customizeHeaders(payload.Headers);
				var transactionOptions = GetTransactionOptions();
				using (var tx = new TransactionScope(TransactionScopeOption.Required, transactionOptions))
				{
					queueManager.Send(destination.Uri, payload);
					tx.Complete();
				}
			}

			var copy = MessageSent;
			if (copy == null)
				return;

			copy(new RhinoQueueCurrentMessageInformation
			{
				AllMessages = msgs,
				Source = Endpoint.Uri,
				Destination = destination.Uri,
				MessageId = messageId,
			});
		}

		private TransactionOptions GetTransactionOptions()
		{
			return new TransactionOptions
			{
				IsolationLevel = Transaction.Current == null ? queueIsolationLevel : Transaction.Current.IsolationLevel,
				Timeout = TransportUtil.GetTransactionTimeout(),
			};
		}

		protected static MessageType GetAppSpecificMarker(object[] msgs)
		{
			var msg = msgs[0];
			if (msg is AdministrativeMessage)
				return MessageType.AdministrativeMessageMarker;
			if (msg is LoadBalancerMessage)
				return MessageType.LoadBalancerMessageMarker;
			return 0;
		}

		public void Send(Endpoint endpoint, DateTime processAgainAt, object[] msgs)
		{
			SendInternal(msgs, endpoint,
				nv =>
				{
					nv["time-to-send"] = processAgainAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
					nv["type"] = MessageType.TimeoutMessageMarker.ToString();
				});
		}

		public void Reply(params object[] messages)
		{
			Send(new Endpoint { Uri = currentMessageInformation.Source }, messages);
		}

		public event Action<CurrentMessageInformation> MessageSent;
		public event Func<CurrentMessageInformation, bool> AdministrativeMessageArrived;
		public event Func<CurrentMessageInformation, bool> MessageArrived;
		public event Action<CurrentMessageInformation, Exception> MessageSerializationException;
		public event Action<CurrentMessageInformation, Exception> MessageProcessingFailure;
		public event Action<CurrentMessageInformation, Exception> MessageProcessingCompleted;
		public event Action<CurrentMessageInformation> BeforeMessageTransactionCommit;
		public event Action<CurrentMessageInformation, Exception> AdministrativeMessageProcessingCompleted;
		public event Action Started;
	}
}
