using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime.ResponseReply;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.Runtime.Routing;

public class MessageRoute : IMessageRoute
{
    private readonly IReplyTracker _replyTracker;

    private static ImHashMap<Type, IList<IEnvelopeRule>> _rulesByMessageType =
        ImHashMap<Type, IList<IEnvelopeRule>>.Empty;

    public MessageRoute(Type messageType, Endpoint endpoint, IReplyTracker replies) : this(endpoint.DefaultSerializer!, endpoint.Agent!,
        endpoint.OutgoingRules.Concat(RulesForMessageType(messageType)), replies)
    {
    }

    public MessageRoute(IMessageSerializer serializer, ISendingAgent sender, IEnumerable<IEnvelopeRule> rules,
        IReplyTracker replyTracker)
    {
        _replyTracker = replyTracker;
        Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        Sender = sender ?? throw new ArgumentNullException(nameof(sender));
        Rules.AddRange(rules);
    }

    public IMessageSerializer Serializer { get; }
    public ISendingAgent Sender { get; }

    public IList<IEnvelopeRule> Rules { get; } = new List<IEnvelopeRule>();

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime)
    {
        var envelope = new Envelope(message, Sender);
        if (options != null && options.ContentType.IsNotEmpty() && options.ContentType != envelope.ContentType)
        {
            envelope.Serializer = runtime.Options.FindSerializer(options.ContentType);
        }

        foreach (var rule in Rules) rule.Modify(envelope);

        // Delivery options win
        options?.Override(envelope);

        // adjust for local, scheduled send
        if (envelope.IsScheduledForLater(DateTimeOffset.Now) && !Sender.SupportsNativeScheduledSend)
        {
            return envelope.ForScheduledSend(localDurableQueue);
        }

        return envelope;
    }

    public static IEnumerable<IEnvelopeRule> RulesForMessageType(Type type)
    {
        if (_rulesByMessageType.TryFind(type, out var rules))
        {
            return rules;
        }

        rules = type.GetAllAttributes<ModifyEnvelopeAttribute>().OfType<IEnvelopeRule>().ToList();
        _rulesByMessageType = _rulesByMessageType.AddOrUpdate(type, rules);

        return rules;
    }
    
    public async Task<T> InvokeAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default,
        TimeSpan? timeout = null) where T : class
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }
        
        timeout ??= 5.Seconds();
        

        var envelope = new Envelope(message, Sender);
        foreach (var rule in Rules) rule.Modify(envelope);
        if (typeof(T) == typeof(Acknowledgement))
        {
            envelope.AckRequested = true;
        }
        else
        {
            envelope.ReplyRequested = typeof(T).ToMessageTypeName();
        }
        
        envelope.DeliverWithin = timeout.Value;
        envelope.Sender = Sender;

        bus.TrackEnvelopeCorrelation(envelope);

        var waiter = _replyTracker.RegisterListener<T>(envelope, cancellation, timeout!.Value);

        await Sender.EnqueueOutgoingAsync(envelope);

        return await waiter;
    }

}