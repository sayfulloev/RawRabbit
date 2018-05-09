﻿using System;
using System.Threading;
using System.Threading.Tasks;
using RawRabbit.Common;
using RawRabbit.Enrichers.MessageContext.Subscribe;
using RawRabbit.Operations.Subscribe.Context;
using RawRabbit.Operations.Subscribe.Middleware;
using RawRabbit.Pipe;
using RawRabbit.Pipe.Middleware;

namespace RawRabbit
{
	public static class SubscribeMessageContextExtension
	{
		public static readonly Action<IPipeBuilder> ConsumePipe = consume => consume
			.Use<StageMarkerMiddleware>(StageMarkerOptions.For(StageMarker.MessageReceived))
			.Use<HeaderDeserializationMiddleware>(new HeaderDeserializationOptions
			{
				HeaderKeyFunc = c => PropertyHeaders.Context,
				HeaderTypeFunc = c => c.GetMessageContextType(),
				ContextSaveAction = (pipeCtx, msgCtx) => pipeCtx.Properties.TryAdd(PipeKey.MessageContext, msgCtx)
			})
			.Use<SubscriptionExceptionMiddleware>(new SubscriptionExceptionOptions
			{
				InnerPipe = p => p
					.Use<BodyDeserializationMiddleware>()
					.Use<StageMarkerMiddleware>(StageMarkerOptions.For(StageMarker.MessageDeserialized))
					.Use<StageMarkerMiddleware>(StageMarkerOptions.For(MessageContextSubscibeStage.MessageContextDeserialized))
					.Use<HandlerInvocationMiddleware>(new HandlerInvocationOptions
					{
						HandlerArgsFunc = context => new[]
						{
							context.GetMessage(),
							context.GetMessageContextResolver()?.Invoke(context) ?? context.GetMessageContext()
						}
					})
			})
			.Use<StageMarkerMiddleware>(StageMarkerOptions.For(StageMarker.HandlerInvoked))
			.Use<ExplicitAckMiddleware>()
			.Use<StageMarkerMiddleware>(StageMarkerOptions.For(StageMarker.MessageAcknowledged));

		public static readonly Action<IPipeBuilder> SubscribePipe = SubscribeMessageExtension.SubscribePipe + (pipe =>
		{
			pipe.Replace<ConsumerMessageHandlerMiddleware, ConsumerMessageHandlerMiddleware>(args: new ConsumeOptions { Pipe = ConsumePipe });
		});

		public static Task<IPipeContext> SubscribeAsync<TMessage, TMessageContext>(this IBusClient client, Func<TMessage, TMessageContext, Task> subscribeMethod, Action<ISubscribeContext> context = null, CancellationToken ct = default(CancellationToken))
		{
			return client.SubscribeAsync<TMessage, TMessageContext>(
					(msg, ctx) => subscribeMethod
						.Invoke(msg, ctx)
						.ContinueWith<Acknowledgement>(t => new Ack(), ct),
					context, ct);
		}

		public static Task<IPipeContext> SubscribeAsync<TMessage, TMessageContext>(
			this IBusClient client,
			Func<TMessage, TMessageContext, Task<Acknowledgement>> subscribeMethod,
			Action<ISubscribeContext> context = null,
			CancellationToken token = default(CancellationToken))
		{
			return client
				.InvokeAsync(
					SubscribePipe,
					ctx =>
					{
						Func<object[], Task<Acknowledgement>> genericHandler = args => subscribeMethod((TMessage)args[0], (TMessageContext)args[1]);

						ctx.Properties.TryAdd(PipeKey.MessageType, typeof(TMessage));
						if (!ctx.Properties.ContainsKey(PipeContextExtensions.PipebasedContextFunc))
						{
							ctx.AddMessageContextType<TMessageContext>();
						}
						ctx.Properties.TryAdd(PipeKey.MessageHandler, genericHandler);
						context?.Invoke(new SubscribeContext(ctx));
					}, token);
		}
	}
}
