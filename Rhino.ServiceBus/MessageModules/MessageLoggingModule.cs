using System;
using System.Messaging;
using Rhino.ServiceBus.Impl;
using Rhino.ServiceBus.Internal;
using Rhino.ServiceBus.Messages;
using Rhino.ServiceBus.Msmq;

namespace Rhino.ServiceBus.MessageModules
{
    public class MessageLoggingModule : IMessageModule
    {
        private readonly IMessageSerializer messageSerializer;
        private readonly IEndpointRouter endpointRouter;
        private readonly Uri logQueue;
        private OpenedQueue queue;

        [ThreadStatic] private static DateTime messageArrival;

        public Uri LogQueue
        {
            get { return logQueue; }
        }

        public MessageLoggingModule(IMessageSerializer messageSerializer, IEndpointRouter endpointRouter, Uri logQueue)
        {
            this.messageSerializer = messageSerializer;
            this.endpointRouter = endpointRouter;
            this.logQueue = logQueue;
        }

		public void Init(ITransport transport, IServiceBus bus)
        {
        	var endpoint = endpointRouter.GetRoutedEndpoint(logQueue);
        	var queueInfo = MsmqUtil.GetQueuePath(endpoint);
			queueInfo.Create();
        	queue = queueInfo.Open(QueueAccessMode.Send);

            transport.MessageArrived += Transport_OnMessageArrived;
            transport.MessageProcessingFailure += Transport_OnMessageProcessingFailure;
            transport.MessageProcessingCompleted += Transport_OnMessageProcessingCompleted;
            transport.MessageSerializationException += Transport_OnMessageSerializationException;
            transport.MessageSent+=Transport_OnMessageSent;
        }

		public void Stop(ITransport transport, IServiceBus bus)
        {
            transport.MessageArrived -= Transport_OnMessageArrived;
            transport.MessageProcessingFailure -= Transport_OnMessageProcessingFailure;
            transport.MessageProcessingCompleted -= Transport_OnMessageProcessingCompleted;
            transport.MessageSerializationException -= Transport_OnMessageSerializationException;
            transport.MessageSent -= Transport_OnMessageSent;

            queue.Dispose();
        }

        private void Transport_OnMessageSent(CurrentMessageInformation info)
        {
            Send(new MessageSentMessage
            {
                MessageId = info.MessageId,
                Source = info.Source,
                Message = info.AllMessages,
                MessageType = info.AllMessages[0].ToString(),
                Timestamp = DateTime.Now,
                Destination = info.Destination
            });
        }

        private void Send(object obj)
        {
            var msg = new Message
            {
            	Label = obj.ToString(),
            	Extension = Guid.NewGuid().ToByteArray()
            };
            messageSerializer.Serialize(new[] { obj }, msg.BodyStream);
            queue.Send(msg);
        }

        private void Transport_OnMessageSerializationException(CurrentMessageInformation info, Exception t)
        {
            Send(new SerializationErrorMessage
            {
                MessageId = info.MessageId,
                Error = t.ToString(),
                Source = info.Source,
            });
        }

         private void Transport_OnMessageProcessingCompleted(CurrentMessageInformation info, Exception ex)
         {
             var timestamp = DateTime.Now;
             Send(new MessageProcessingCompletedMessage
            {
                Timestamp = timestamp,
                Duration = timestamp - messageArrival,
                MessageType = info.Message.ToString(),
                MessageId = info.MessageId,
                Source = info.Source,
            });
         }

        internal void Transport_OnMessageProcessingFailure(CurrentMessageInformation info, Exception e)
        {
            string messageType = (info.Message ?? "no message").ToString();
            SendInSingleTransaction(new MessageProcessingFailedMessage
            {
                ErrorText = e.ToString(),
                Timestamp = DateTime.Now,
                MessageType = messageType,
                MessageId = info.MessageId,
                Source = info.Source,
                Message = info.Message
            });
        }

        private void SendInSingleTransaction(object msg)
    	{
    		var message = new Message
    		{
    			Label = msg.ToString(),
                Extension = Guid.NewGuid().ToByteArray()
    		};
			messageSerializer.Serialize(new[]{msg},message.BodyStream);
    		queue.SendInSingleTransaction(message);
    	}

    	private bool Transport_OnMessageArrived(CurrentMessageInformation info)
        {
    	    messageArrival = DateTime.Now;
            Send(new MessageArrivedMessage
            {
                Timestamp = messageArrival,
                MessageType = info.Message.ToString(),
                MessageId = info.MessageId,
                Source = info.Source,
                Message = info.Message
            });
            return false;
        }
    }
}