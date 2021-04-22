using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using static RSocket.RSocketProtocol;

namespace RSocket.Channels
{
	public abstract partial class Channel : IChannel
	{
		bool _disposed = false;
		int _initialOutgoingRequest = 0;

		TaskCompletionSource<bool> _waitIncomingCompleteHandler = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> _waitOutgoingCompleteHandler = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		IncomingReceiver _incomingReceiver;

		Lazy<IPublisher<Payload>> _lazyOutgoing;
		Lazy<(ISubscription Subscription, IObserver<Payload> Subscriber)> _lazyOutgoingSubscriber;

		bool _incomingFinished;
		bool _outgoingFinished;

		public bool IncomingFinished { get { return this._incomingFinished; } }
		public bool OutgoingFinished { get { return this._outgoingFinished; } }

		Channel(RSocket socket)
		{
			this.Socket = socket;

			this._incomingReceiver = new IncomingReceiver();
			this.Incoming = this.CreateIncoming();
			this._lazyOutgoing = new Lazy<IPublisher<Payload>>(this.CreateOutgoingLazy, LazyThreadSafetyMode.ExecutionAndPublication);
			this._lazyOutgoingSubscriber = new Lazy<(ISubscription Subscription, IObserver<Payload> Subscriber)>(this.SubscribeOutgoing, LazyThreadSafetyMode.ExecutionAndPublication);
		}
		protected Channel(RSocket socket, int channelId) : this(socket)
		{
			this.ChannelId = channelId;
		}
		protected Channel(RSocket socket, int channelId, int initialOutgoingRequest) : this(socket, channelId)
		{
			this._initialOutgoingRequest = initialOutgoingRequest;
		}

		public RSocket Socket { get; set; }
		public int ChannelId { get; set; }

		public IPublisher<Payload> Incoming { get; private set; }
		public IPublisher<Payload> Outgoing { get { return this._lazyOutgoing.Value; } }

		protected IObserver<Payload> IncomingSubscriber { get { return this._incomingReceiver; } }

		ISubscription _outgoingSubscription;
		ISubscription OutgoingSubscription { get { return this._lazyOutgoingSubscriber.Value.Subscription; } }

		protected virtual IPublisher<Payload> CreateIncoming()
		{
			return new IncomingStream(this._incomingReceiver, this);
		}
		protected virtual IPublisher<Payload> CreateOutgoing()
		{
			return new Publisher<Payload>();
		}

		public void FinishIncoming()
		{
			if (this._incomingFinished)
				return;

			this._incomingFinished = true;

			this._incomingReceiver.Dispose();
			this._waitIncomingCompleteHandler.TrySetResult(true);
		}
		public void FinishOutgoing()
		{
			if (this._outgoingFinished)
				return;

			this._outgoingFinished = true;

			this._outgoingSubscription?.Dispose();
			this._waitOutgoingCompleteHandler.TrySetResult(true);
		}

		IPublisher<Payload> CreateOutgoingLazy()
		{
			try
			{
				return this.CreateOutgoing();
			}
			catch (Exception ex)
			{
				this.OnOutgoingError(ex);
				return new Publisher<Payload>();
			}
		}
		(ISubscription Subscription, IObserver<Payload> Subscriber) SubscribeOutgoing()
		{
			var subscriber = new DefaultOutgoingSubscriber(this);
			var subscription = this.Outgoing.Subscribe(subscriber);
			this._outgoingSubscription = subscription;

			if (this._outgoingFinished) // In case another thread finishes outgoing
				this._outgoingSubscription.Dispose();

			return (subscription, subscriber);
		}

		object EnsureHaveBeenReady()
		{
			return this.OutgoingSubscription;
		}


		public virtual void OnOutgoingNext(Payload payload)
		{
			if (this._outgoingFinished)
				return;

			this.Socket.SendPayload(this.ChannelId, data: payload.Data, metadata: payload.Metadata, complete: false, next: true);
		}
		public virtual void OnOutgoingCompleted()
		{
			if (this._outgoingFinished)
				return;

			this.Socket.SendPayload(this.ChannelId, complete: true, next: false);
			this.FinishOutgoing();
		}
		public virtual void OnOutgoingError(Exception error)
		{
			this.FinishOutgoing();
			this.IncomingSubscriber.OnError(new OperationCanceledException("Outbound has terminated with an error.", error));
			this.Socket.SendError(this.ChannelId, ErrorCodes.Application_Error, $"{error.Message}\n{error.StackTrace}");
		}


		public virtual void HandlePayload(RSocketProtocol.Payload message, ReadOnlySequence<byte> metadata, ReadOnlySequence<byte> data)
		{
			this.EnsureHaveBeenReady();
			this.HandlePayloadCore(message, metadata, data);
		}
		protected virtual void HandlePayloadCore(RSocketProtocol.Payload message, ReadOnlySequence<byte> metadata, ReadOnlySequence<byte> data)
		{
			if (this._incomingFinished)
				return;

			var incomingReceiver = this._incomingReceiver;

			if (message.IsNext)
			{
				incomingReceiver.OnNext(new Payload(data, metadata));
			}

			if (message.IsComplete)
			{
				incomingReceiver.OnCompleted();
				this.FinishIncoming();
			}
		}

		public virtual void HandleError(RSocketProtocol.Error message)
		{
			this.EnsureHaveBeenReady();
			this.HandleErrorCore(message);
			this.FinishIncoming();
		}
		protected virtual void HandleErrorCore(RSocketProtocol.Error message)
		{
			this.FinishOutgoing();
			this._incomingReceiver.OnError(message.MakeException());
		}

		public virtual void HandleRequestN(RSocketProtocol.RequestN message)
		{
			this.HandleRequestN(message.RequestNumber);
		}
		public virtual void HandleRequestN(int n)
		{
			this.EnsureHaveBeenReady();
			this.HandleRequestNCore(n);
		}
		protected virtual void HandleRequestNCore(int n)
		{
			if (this._outgoingFinished)
				return;

			this.OutgoingSubscription.Request(n);
		}

		public virtual void HandleCancel(RSocketProtocol.Cancel message)
		{
#if DEBUG
			Console.WriteLine($"Handling cancel message...............stream[{this.ChannelId}]");
#endif
			this.EnsureHaveBeenReady();
			this.HandleCancelCore();
		}
		protected virtual void HandleCancelCore()
		{
			this.FinishOutgoing();
		}


		public virtual void OnIncomingSubscriberOnNextError()
		{
			this.OnIncomingSubscriptionCanceled();
		}
		public virtual void OnIncomingSubscriptionCanceled()
		{
			if (this._incomingFinished)
				return;

			this.FinishIncoming();
			this.SendCancelFrame();
		}
		public void OnIncomingSubscriptionRequestN(int n)
		{
			if (this._incomingFinished)
				return;

			this.Socket.SendRequestN(this.ChannelId, n);
		}
		internal void SendCancelFrame()
		{
#if DEBUG
			Console.WriteLine($"Sending cancel frame...............stream[{this.ChannelId}]");
#endif
			this.Socket.SendCancel(this.ChannelId);
		}


		public virtual async Task ToTask()
		{
			if (this._initialOutgoingRequest > 0)
				this.HandleRequestN(this._initialOutgoingRequest);
			await Task.WhenAll(this._waitIncomingCompleteHandler.Task, this._waitOutgoingCompleteHandler.Task);
		}

		public void Dispose()
		{
			if (this._disposed)
				return;

			this.TriggerErrorIfIncomingUnfinished();
			this.FinishIncoming();
			this.FinishOutgoing();

			try
			{
				this.Dispose(true);
			}
			catch
			{
			}

			this._disposed = true;
		}
		protected virtual void Dispose(bool disposing)
		{
		}
		void TriggerErrorIfIncomingUnfinished()
		{
			if (this._incomingFinished)
				return;

			try
			{
				this.IncomingSubscriber.OnError(new OperationCanceledException("Channel has terminated."));
			}
			catch
			{
			}
		}
	}
}
