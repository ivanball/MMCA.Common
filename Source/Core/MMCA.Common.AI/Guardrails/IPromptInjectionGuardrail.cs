namespace MMCA.Common.AI.Guardrails;

/// <summary>
/// Marks a guardrail or redactor that defends against prompt injection in user content, which is
/// what <see cref="AiSettings.RequireGuardrail"/> asks an enabled host to register.
/// <para>
/// A marker, not a contract with members: the type that carries it is still registered as an
/// <see cref="Chat.IChatGuardrail"/> or an <see cref="IChatRequestRedactor"/>, and
/// <c>AddMmcaChatClient</c> reads the implementation type of those registrations to find it. The
/// requirement is about the threat, not about having something in the layer: a PII redactor strips
/// contact details but lets "ignore previous instructions" straight through, so on its own it does
/// not satisfy the requirement.
/// </para>
/// </summary>
/// <remarks>
/// <see cref="ContentPolicyGuardrail"/> (registered by <c>AddContentPolicyGuardrail(configuration)</c>)
/// is the implementation this framework ships. An application that writes its own injection
/// defense implements this interface on that type and registers it with a concrete implementation
/// type, an instance, or a typed factory, so the registration-time check can see what it is.
/// </remarks>
public interface IPromptInjectionGuardrail;
