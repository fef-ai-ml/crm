using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Crm.Segmentation.Domain.Rules;

/// <summary>Writes a <see cref="RuleNode"/> tree to its canonical JSON form: fixed key order, no whitespace,
/// numbers in shortest round-trip form (H20). Two trees that are equal produce identical canonical text, which
/// is what <see cref="DefinitionHash"/> hashes and what the `Definition` column stores.</summary>
public static class CanonicalRuleWriter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(RuleNode tree)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            WriteNode(writer, tree);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter writer, RuleNode node)
    {
        switch (node)
        {
            case GroupNode group:
                writer.WriteStartObject();
                writer.WriteString("op", ToJson(group.Op));
                writer.WriteStartArray("children");
                foreach (var child in group.Children)
                {
                    WriteNode(writer, child);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                break;

            case AttributeNode attribute:
                writer.WriteStartObject();
                writer.WriteString("type", "attribute");
                writer.WriteString("key", attribute.Key);
                writer.WriteString("cmp", ToJson(attribute.Cmp));
                if (attribute.Value is not null)
                {
                    writer.WritePropertyName("value");
                    WriteValue(writer, attribute.Value);
                }

                writer.WriteEndObject();
                break;

            case EventNode @event:
                writer.WriteStartObject();
                writer.WriteString("type", "event");
                writer.WriteString("name", @event.Name);
                writer.WriteString("cmp", ToJson(@event.Cmp));
                writer.WriteNumber("count", @event.Count);
                writer.WriteString("window", @event.Window.Text);
                writer.WriteEndObject();
                break;

            default:
                throw new NotSupportedException($"Unknown rule node type '{node.GetType()}'.");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, RuleValue value)
    {
        switch (value)
        {
            case RuleValue.Text text:
                writer.WriteStringValue(text.Value);
                break;

            case RuleValue.Number number:
                writer.WriteRawValue(FormatNumber(number.Value), skipInputValidation: true);
                break;

            case RuleValue.Bool boolean:
                writer.WriteBooleanValue(boolean.Value);
                break;

            case RuleValue.TextList textList:
                writer.WriteStartArray();
                foreach (var item in textList.Values)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
                break;

            case RuleValue.NumberList numberList:
                writer.WriteStartArray();
                foreach (var item in numberList.Values)
                {
                    writer.WriteRawValue(FormatNumber(item), skipInputValidation: true);
                }

                writer.WriteEndArray();
                break;

            default:
                throw new NotSupportedException($"Unknown rule value type '{value.GetType()}'.");
        }
    }

    private static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string ToJson(GroupOp op) => op switch
    {
        GroupOp.And => "and",
        GroupOp.Or => "or",
        GroupOp.Not => "not",
        _ => throw new NotSupportedException($"Unknown group operator '{op}'."),
    };

    private static string ToJson(AttributeCmp cmp) => cmp switch
    {
        AttributeCmp.Eq => "eq",
        AttributeCmp.Neq => "neq",
        AttributeCmp.In => "in",
        AttributeCmp.Nin => "nin",
        AttributeCmp.Gt => "gt",
        AttributeCmp.Gte => "gte",
        AttributeCmp.Lt => "lt",
        AttributeCmp.Lte => "lte",
        AttributeCmp.Contains => "contains",
        AttributeCmp.Exists => "exists",
        AttributeCmp.NotExists => "not_exists",
        _ => throw new NotSupportedException($"Unknown attribute comparator '{cmp}'."),
    };

    private static string ToJson(EventCmp cmp) => cmp switch
    {
        EventCmp.Eq => "eq",
        EventCmp.Gte => "gte",
        EventCmp.Lte => "lte",
        _ => throw new NotSupportedException($"Unknown event comparator '{cmp}'."),
    };
}
