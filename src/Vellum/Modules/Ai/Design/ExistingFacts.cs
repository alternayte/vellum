using System.Globalization;
using System.Text;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Modelling;

namespace Vellum.Modules.Ai.Design;

/// <summary>A thing that a model proposed, with every reference as the ID of a thing that exists. A null ID is a reference to a thing that does not exist yet.</summary>
internal sealed record ProposedThing(
    string Kind, string Name, string? ElementKind = null, Guid? ParentId = null, bool ParentIsNew = false,
    Guid? OwnerId = null, Guid? ChannelId = null, Guid? ConsumerId = null,
    Guid? SourceChannelId = null, Guid? TargetChannelId = null, string? RoutingKey = null);

/// <summary>What a proposed thing is in the model already.</summary>
internal abstract record ExistingMatch
{
    private ExistingMatch() { }

    public sealed record None : ExistingMatch;
    public sealed record One(Guid Id, string Name) : ExistingMatch;
    /// <summary>More than one thing fits. The server does not choose.</summary>
    public sealed record Ambiguous(IReadOnlyList<string> Names) : ExistingMatch;
}

/// <summary>
/// Says whether a thing that a model proposes is a thing the branch holds already. The prompt
/// asks the model not to propose such a thing; this check is what keeps a second
/// <c>Orders</c> out of the model. It uses the import module's own rule
/// (<see cref="ModelIndex.MatchKey"/>), so an AI proposal and a scan agree on what "the same
/// thing" is:
/// <list type="bullet">
/// <item>element: the same kind and the same normalised name (white space folded, upper
/// case). When the proposal names an existing parent, only an element with that parent fits.</item>
/// <item>operation: the same owner and the same method and route template, with parameter
/// names and constraints ignored (<c>POST /orders/{id}</c> is <c>post /orders/{orderId:guid}/</c>).</item>
/// <item>channel, message: the same normalised name.</item>
/// <item>subscription: the same channel and consumer.</item>
/// <item>binding: the same source, target and routing key.</item>
/// </list>
/// A thing whose owner, channel, consumer, source or target is itself new cannot exist already.
/// </summary>
internal static class ExistingFacts
{
    public static ExistingMatch Find(ModelState model, ProposedThing thing, Guid? except = null)
    {
        IEnumerable<(Guid Id, string Name)> candidates;
        switch (thing.Kind)
        {
            case AliasKinds.Element:
                if (!ModelEnum.TryParseDefined(thing.ElementKind, out ElementKind elementKind))
                    return new ExistingMatch.None();
                var elementKey = Key(EntityKinds.Element, (Field.Kind, elementKind.ToString().ToLowerInvariant()), (Field.Name, thing.Name));
                candidates = model.Elements.Values
                    .Where(e => ModelIndex.MatchKey(EntityKinds.Element, ModelIndex.Of(e).Values) == elementKey)
                    .Where(e => thing.ParentId is not { } parent || e.ParentId?.Value == parent)
                    // An element under a parent that is new cannot be an element that exists under another parent.
                    .Where(_ => !thing.ParentIsNew)
                    .Select(e => (e.Id.Value, e.Name));
                break;

            case AliasKinds.Operation:
                if (thing.OwnerId is not { } owner)
                    return new ExistingMatch.None();
                var operationKey = Key(EntityKinds.Operation, (Field.OwnerId, new ElementId(owner).ToString()), (Field.Name, thing.Name));
                candidates = model.Operations.Values
                    .Where(o => ModelIndex.MatchKey(EntityKinds.Operation, ModelIndex.Of(o).Values) == operationKey)
                    .Select(o => (o.Id.Value, o.Name));
                break;

            case AliasKinds.Channel:
                var channelKey = Key(EntityKinds.Channel, (Field.Name, thing.Name));
                candidates = model.Channels.Values
                    .Where(c => ModelIndex.MatchKey(EntityKinds.Channel, ModelIndex.Of(c).Values) == channelKey)
                    .Select(c => (c.Id.Value, c.Name));
                break;

            case AliasKinds.Message:
                var messageKey = Key(EntityKinds.Message, (Field.Name, thing.Name));
                candidates = model.Messages.Values
                    .Where(m => ModelIndex.MatchKey(EntityKinds.Message, ModelIndex.Of(m).Values) == messageKey)
                    .Select(m => (m.Id.Value, m.Name));
                break;

            case AliasKinds.Subscription:
                if (thing is not { ChannelId: { } channel, ConsumerId: { } consumer })
                    return new ExistingMatch.None();
                candidates = model.Subscriptions.Values
                    .Where(s => s.ChannelId.Value == channel && s.ConsumerId.Value == consumer)
                    .Select(s => (s.Id.Value, ContractDecider.SubscriptionLabel(model, s)));
                break;

            case AliasKinds.Binding:
                if (thing is not { SourceChannelId: { } source, TargetChannelId: { } target })
                    return new ExistingMatch.None();
                candidates = model.Bindings.Values
                    .Where(b => b.SourceChannelId.Value == source && b.TargetChannelId.Value == target && b.RoutingKey == thing.RoutingKey)
                    .Select(b => (b.Id.Value, ContractDecider.BindingLabel(model, b)));
                break;

            default:
                return new ExistingMatch.None();
        }

        var found = candidates.Where(c => c.Id != except).OrderBy(c => c.Id).Take(5).ToList();
        return found.Count switch
        {
            0 => new ExistingMatch.None(),
            1 => new ExistingMatch.One(found[0].Id, found[0].Name),
            _ => new ExistingMatch.Ambiguous([.. found.Select(c => c.Name)]),
        };
    }

    private static string Key(string entityKind, params (string Field, string? Value)[] values) =>
        ModelIndex.MatchKey(entityKind, values.ToDictionary(v => v.Field, v => v.Value));
}

/// <summary>
/// The rule for a name that a model proposes. A person's own input keeps the rule of the
/// modelling endpoints; this one is stricter because nobody typed the name.
/// <list type="number">
/// <item>The name is normalised to Unicode form NFC.</item>
/// <item>A name with a character of Unicode category Cf (format) is refused. That category
/// holds the bidirectional controls, the zero-width space, joiner and non-joiner, the
/// byte-order mark and the soft hyphen: characters that change how a name looks or compares
/// without showing.</item>
/// <item>A name that is not the name of an existing thing of the same kind, and that has the
/// same skeleton as one, is refused. The skeleton is the name in form NFKC with each Cyrillic
/// or Greek letter of <see cref="LookAlikes"/> replaced by the Latin letter it looks like,
/// then folded as the import module folds a name. So <c>Оrders</c> with a Cyrillic first
/// letter is refused beside <c>Orders</c>.</item>
/// </list>
/// The table of look-alikes is small on purpose: it holds the letters that are drawn the same
/// as a Latin letter in common fonts, and no other script.
/// </summary>
internal static class ProposedNames
{
    private static readonly Dictionary<char, char> LookAlikes = new()
    {
        // Cyrillic
        ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M', ['Н'] = 'H', ['О'] = 'O', ['Р'] = 'P', ['С'] = 'C',
        ['Т'] = 'T', ['Х'] = 'X', ['У'] = 'Y', ['І'] = 'I', ['Ј'] = 'J', ['Ѕ'] = 'S',
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['ј'] = 'j',
        ['ѕ'] = 's', ['ԁ'] = 'd', ['һ'] = 'h', ['ԛ'] = 'q', ['ԝ'] = 'w',
        // Greek
        ['Α'] = 'A', ['Β'] = 'B', ['Ε'] = 'E', ['Ζ'] = 'Z', ['Η'] = 'H', ['Ι'] = 'I', ['Κ'] = 'K', ['Μ'] = 'M', ['Ν'] = 'N',
        ['Ο'] = 'O', ['Ρ'] = 'P', ['Τ'] = 'T', ['Υ'] = 'Y', ['Χ'] = 'X', ['ο'] = 'o', ['ν'] = 'v', ['ι'] = 'i', ['κ'] = 'k',
    };

    /// <summary>The name in form NFC. Text that is not valid Unicode is returned as it is; the text rule refuses it later.</summary>
    public static string Normalize(string name)
    {
        try
        {
            return name.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return name;
        }
    }

    /// <summary>Why the name may not be used for a new thing of <paramref name="kind"/>, or null.</summary>
    /// <param name="ownerId">For an operation: its owner. Only that owner's operations are compared.</param>
    public static string? Problem(ModelState model, string kind, string name, Guid? ownerId = null)
    {
        if (name.Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            return "The name holds a character that does not show, such as a zero-width space or a direction mark.";

        IEnumerable<string> existing = kind switch
        {
            AliasKinds.Element => model.Elements.Values.Select(e => e.Name),
            AliasKinds.Operation => model.Operations.Values.Where(o => ownerId is null || o.OwnerId.Value == ownerId).Select(o => o.Name),
            AliasKinds.Channel => model.Channels.Values.Select(c => c.Name),
            AliasKinds.Message => model.Messages.Values.Select(m => m.Name),
            _ => [],
        };

        var folded = ModelIndex.NormalName(name);
        var skeleton = Skeleton(name);
        var twin = existing.FirstOrDefault(other => ModelIndex.NormalName(other) != folded && Skeleton(other) == skeleton);
        return twin is null ? null : $"The name looks the same as the existing name '{twin}' and is written with other letters.";
    }

    internal static string Skeleton(string name)
    {
        string compatible;
        try
        {
            compatible = name.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            compatible = name;
        }

        var latin = new StringBuilder(compatible.Length);
        foreach (var c in compatible)
            latin.Append(LookAlikes.GetValueOrDefault(c, c));
        return ModelIndex.NormalName(latin.ToString());
    }
}
