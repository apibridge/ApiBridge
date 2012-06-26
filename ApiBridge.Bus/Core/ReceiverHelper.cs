﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ApiBridge.Bus.Interfaces;
using Microsoft.Practices.TransientFaultHandling;
using Autofac;
using System.Threading;
using Microsoft.ServiceBus.Messaging;
using System.IO;
using ApiBridge.Handlers.Interfaces;

namespace ApiBridge.Bus.Core
{
    class ReceiverHelper
    {
        IBusConfiguration config;
        RetryPolicy retryPolicy;
        BusReceiverState data;

        int failCounter = 0;

        public BusReceiverState Data
        {
            get
            {
                return data;
            }
        }

        public ReceiverHelper(IBusConfiguration config, RetryPolicy retryPolicy, BusReceiverState data)
        {
            this.config = config;
            this.retryPolicy = retryPolicy;
            this.data = data;
        }

        public void ProcessMessagesForSubscription()
        {
            try
            {
                Guard.ArgumentNotNull(data, "data");

                //TODO create a cache for object creation.
                var gt = typeof(ICommandReceiver<>).MakeGenericType(data.EndPointData.MessageType);

                //set up the methodinfo
                var methodInfo = data.EndPointData.DeclaredType.GetMethod("Handle", new Type[] { gt });

                var serializer = config.Container.Resolve<IServiceBusSerializer>();

                var waitTimeout = TimeSpan.FromSeconds(30);

                // Declare an action acting as a callback whenever a message arrives on a queue.
                AsyncCallback completeReceive = null;

                // Declare an action acting as a callback whenever a non-transient exception occurs while receiving or processing messages.
                Action<Exception> recoverReceive = null;

                // Declare an action implementing the main processing logic for received messages.
                Action<ReceiveState> processMessage = ((receiveState) =>
                {
                    // Put your custom processing logic here. DO NOT swallow any exceptions.
                    ProcessMessageCallBack(receiveState);
                });

                bool messageReceived = false;

                bool lastAttemptWasError = false;

                // Declare an action responsible for the core operations in the message receive loop.
                Action receiveMessage = (() =>
                {
                    // Use a retry policy to execute the Receive action in an asynchronous and reliable fashion.
                    retryPolicy.ExecuteAction
                    (
                        (cb) =>
                        {
                            if (lastAttemptWasError)
                            {
                                if (Data.EndPointData.AttributeData.PauseTimeIfErrorWasThrown > 0)
                                {
                                    Thread.Sleep(Data.EndPointData.AttributeData.PauseTimeIfErrorWasThrown);
                                }
                                else
                                {
                                    Thread.Sleep(1000);
                                }
                            }
                            // Start receiving a new message asynchronously.
                            data.Client.BeginReceive(waitTimeout, cb, null);
                        },
                        (ar) =>
                        {
                            messageReceived = false;
                            // Make sure we are not told to stop receiving while we were waiting for a new message.
                            if (!data.CancelToken.IsCancellationRequested)
                            {
                                BrokeredMessage msg = null;
                                try
                                {
                                    // Complete the asynchronous operation. This may throw an exception that will be handled internally by retry policy.
                                    msg = data.Client.EndReceive(ar);

                                    // Check if we actually received any messages.
                                    if (msg != null)
                                    {
                                        // Make sure we are not told to stop receiving while we were waiting for a new message.
                                        if (!data.CancelToken.IsCancellationRequested)
                                        {
                                            // Process the received message.
                                            messageReceived = true;
                                            var receiveState = new ReceiveState(data, methodInfo, serializer, msg);
                                            processMessage(receiveState);

                                            // With PeekLock mode, we should mark the processed message as completed.
                                            if (data.Client.Mode == ReceiveMode.PeekLock)
                                            {
                                                // Mark brokered message as completed at which point it's removed from the queue.
                                                SafeComplete(msg);
                                            }
                                        }
                                    }
                                }
                                catch (Exception)
                                {
                                    //do nothing
                                    throw;
                                }
                                finally
                                {
                                    // With PeekLock mode, we should mark the failed message as abandoned.
                                    if (msg != null)
                                    {
                                        if (data.Client.Mode == ReceiveMode.PeekLock)
                                        {
                                            // Abandons a brokered message. This will cause Service Bus to unlock the message and make it available 
                                            // to be received again, either by the same consumer or by another completing consumer.
                                            SafeAbandon(msg);
                                        }
                                        msg.Dispose();
                                    }
                                }
                            }
                            // Invoke a custom callback method to indicate that we have completed an iteration in the message receive loop.
                            completeReceive(ar);
                        },
                        () =>
                        {
                            //do nothing, we completed.
                        },
                        (ex) =>
                        {
                            // Invoke a custom action to indicate that we have encountered an exception and
                            // need further decision as to whether to continue receiving messages.
                            recoverReceive(ex);
                        });
                });

                // Initialize a custom action acting as a callback whenever a message arrives on a queue.
                completeReceive = ((ar) =>
                {
                    lastAttemptWasError = false;
                    if (!data.CancelToken.IsCancellationRequested)
                    {
                        // Continue receiving and processing new messages until we are told to stop.
                        receiveMessage();
                    }
                    else
                    {
                        data.SetMessageLoopCompleted();
                    }
                });

                // Initialize a custom action acting as a callback whenever a non-transient exception occurs while receiving or processing messages.
                recoverReceive = ((ex) =>
                {
                    // Just log an exception. Do not allow an unhandled exception to terminate the message receive loop abnormally.
                    lastAttemptWasError = true;

                    if (!data.CancelToken.IsCancellationRequested)
                    {
                        // Continue receiving and processing new messages until we are told to stop regardless of any exceptions.
                        receiveMessage();
                    }
                    else
                    {
                        data.SetMessageLoopCompleted();
                    }
                });

                // Start receiving messages asynchronously.
                receiveMessage();
            }
            catch (Exception ex)
            {
                failCounter++;
                if (failCounter < 100)
                {
                    //try again
                    ProcessMessagesForSubscription();
                }
            }

        }

        void ProcessMessageCallBack(ReceiveState state)
        {
            Guard.ArgumentNotNull(state, "state");
            string objectTypeName = string.Empty;

            try
            {
                IDictionary<string, object> values = new Dictionary<string, object>();

                if (state.Message.Properties != null)
                {
                    foreach (var item in state.Message.Properties)
                    {
                        if (item.Key != ReceiverBase.TYPE_HEADER_NAME)
                        {
                            values.Add(item);
                        }
                    }
                }

                using (var serial = state.CreateSerializer())
                {
                    var stream = state.Message.GetBody<Stream>();
                    stream.Position = 0;
                    object msg = serial.Deserialize(stream, state.Data.EndPointData.MessageType);
                    if (msg != null)
                    {
                        // string based json serializer
                        //object msg = serial.DeserializeJson(state.Message.GetBody<string>(), state.Data.EndPointData.MessageType);

                        //TODO create a cache for object creation.
                        var gt = typeof(CommandReceived<>).MakeGenericType(state.Data.EndPointData.MessageType);

                        object receivedMessage = Activator.CreateInstance(gt, new object[] { state.Message, msg, values });

                        objectTypeName = receivedMessage.GetType().FullName;
                        var handler = config.Container.Resolve(state.Data.EndPointData.DeclaredType);
                        state.MethodInfo.Invoke(handler, new object[] { receivedMessage });
                    }
                }
            }
            catch (Exception ex)
            {
                if (state.Message.DeliveryCount >= state.Data.EndPointData.AttributeData.MaxRetries)
                {
                    if (state.Data.EndPointData.AttributeData.DeadLetterAfterMaxRetries)
                    {
                        SafeDeadLetter(state.Message, ex.Message);
                    }
                    else
                    {
                        SafeComplete(state.Message);
                    }
                }
                throw;
            }
        }

        static bool SafeDeadLetter(BrokeredMessage msg, string reason)
        {
            try
            {
                // Mark brokered message as complete.
                msg.DeadLetter(reason, "Max retries Exceeded.");

                // Return a result indicating that the message has been completed successfully.
                return true;
            }
            catch (MessageLockLostException)
            {
                // It's too late to compensate the loss of a message lock. We should just ignore it so that it does not break the receive loop.
                // We should be prepared to receive the same message again.
            }
            catch (MessagingException)
            {
                // There is nothing we can do as the connection may have been lost, or the underlying topic/subscription may have been removed.
                // If Complete() fails with this exception, the only recourse is to prepare to receive another message (possibly the same one).
            }

            return false;
        }

        static bool SafeComplete(BrokeredMessage msg)
        {
            try
            {
                // Mark brokered message as complete.
                msg.Complete();

                // Return a result indicating that the message has been completed successfully.
                return true;
            }
            catch (MessageLockLostException)
            {
                // It's too late to compensate the loss of a message lock. We should just ignore it so that it does not break the receive loop.
                // We should be prepared to receive the same message again.
            }
            catch (MessagingException)
            {
                // There is nothing we can do as the connection may have been lost, or the underlying topic/subscription may have been removed.
                // If Complete() fails with this exception, the only recourse is to prepare to receive another message (possibly the same one).
            }

            return false;
        }

        static bool SafeAbandon(BrokeredMessage msg)
        {
            try
            {
                // Abandons a brokered message. This will cause the Service Bus to unlock the message and make it available to be received again, 
                // either by the same consumer or by another competing consumer.
                msg.Abandon();

                // Return a result indicating that the message has been abandoned successfully.
                return true;
            }
            catch (MessageLockLostException)
            {
                // It's too late to compensate the loss of a message lock. We should just ignore it so that it does not break the receive loop.
                // We should be prepared to receive the same message again.
            }
            catch (MessagingException)
            {
                // There is nothing we can do as the connection may have been lost, or the underlying topic/subscription may have been removed.
                // If Abandon() fails with this exception, the only recourse is to receive another message (possibly the same one).
            }

            return false;
        }
    }
}
