// The browser half of the passkey ceremonies (WebAuthn, ADR-139), imported by
// BrowserPasskeyCapability. Options arrive and credentials leave as JSON in the WebAuthn "*JSON"
// shapes (binary members base64url-encoded), so .NET and the API never handle an ArrayBuffer.
// A cancelled or refused prompt answers null rather than throwing.

let pendingAutofill = null;

function toBuffer(base64url) {
    const base64 = base64url.replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4);
    const binary = atob(padded);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes.buffer;
}

function toBase64Url(buffer) {
    if (!buffer) {
        return null;
    }
    const bytes = new Uint8Array(buffer);
    let binary = '';
    for (let i = 0; i < bytes.length; i++) {
        binary += String.fromCharCode(bytes[i]);
    }
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function withDescriptorIds(descriptors) {
    return (descriptors || []).map(d => ({ ...d, id: toBuffer(d.id) }));
}

function creationOptions(json) {
    return {
        ...json,
        challenge: toBuffer(json.challenge),
        user: { ...json.user, id: toBuffer(json.user.id) },
        excludeCredentials: withDescriptorIds(json.excludeCredentials),
    };
}

function requestOptions(json) {
    return {
        ...json,
        challenge: toBuffer(json.challenge),
        allowCredentials: withDescriptorIds(json.allowCredentials),
    };
}

function extensionResults(credential) {
    return typeof credential.getClientExtensionResults === 'function' ? credential.getClientExtensionResults() : {};
}

function attestationJson(credential) {
    const response = credential.response;
    return {
        id: credential.id,
        rawId: toBase64Url(credential.rawId),
        type: credential.type,
        response: {
            attestationObject: toBase64Url(response.attestationObject),
            clientDataJSON: toBase64Url(response.clientDataJSON),
            transports: typeof response.getTransports === 'function' ? response.getTransports() : [],
        },
        clientExtensionResults: extensionResults(credential),
    };
}

function assertionJson(credential) {
    const response = credential.response;
    return {
        id: credential.id,
        rawId: toBase64Url(credential.rawId),
        type: credential.type,
        response: {
            authenticatorData: toBase64Url(response.authenticatorData),
            clientDataJSON: toBase64Url(response.clientDataJSON),
            signature: toBase64Url(response.signature),
            userHandle: toBase64Url(response.userHandle),
        },
        clientExtensionResults: extensionResults(credential),
    };
}

function isSupported() {
    return typeof window.PublicKeyCredential === 'function' && !!navigator.credentials;
}

// NotAllowedError is the user dismissing the prompt (or its timeout); AbortError is a cancelled
// autofill. Both are an ordinary "no credential", never a fault.
function isCancellation(error) {
    return !!error && (error.name === 'NotAllowedError' || error.name === 'AbortError');
}

export function cancelAutofill() {
    if (pendingAutofill) {
        pendingAutofill.abort();
        pendingAutofill = null;
    }
}

export async function create(options) {
    if (!isSupported()) {
        return null;
    }
    cancelAutofill();
    try {
        const credential = await navigator.credentials.create({ publicKey: creationOptions(options) });
        return credential ? attestationJson(credential) : null;
    } catch (error) {
        if (isCancellation(error)) {
            return null;
        }
        throw error;
    }
}

export async function get(options) {
    if (!isSupported()) {
        return null;
    }
    // A pending autofill request would make the browser refuse this modal one.
    cancelAutofill();
    try {
        const credential = await navigator.credentials.get({ publicKey: requestOptions(options) });
        return credential ? assertionJson(credential) : null;
    } catch (error) {
        if (isCancellation(error)) {
            return null;
        }
        throw error;
    }
}

// Whether this browser can run passkey autofill (conditional mediation). The sign-in page asks this
// BEFORE it requests sign-in options for autofill, so a browser without it stores no server challenge.
export async function isAutofillAvailable() {
    if (!isSupported() || typeof PublicKeyCredential.isConditionalMediationAvailable !== 'function') {
        return false;
    }
    return await PublicKeyCredential.isConditionalMediationAvailable();
}

// Conditional mediation: the browser lists the user's passkeys among the email field's autofill
// suggestions (the field carries autocomplete="... webauthn"). Resolves at once with whether it
// started; the pick, if any, arrives later through the .NET callback.
export async function startAutofill(options, dotNetCallback) {
    if (!(await isAutofillAvailable())) {
        return false;
    }

    cancelAutofill();
    const controller = new AbortController();
    pendingAutofill = controller;
    navigator.credentials
        .get({ mediation: 'conditional', publicKey: requestOptions(options), signal: controller.signal })
        .then(credential => {
            if (pendingAutofill === controller) {
                pendingAutofill = null;
            }
            if (credential) {
                return dotNetCallback.invokeMethodAsync('OnAutofillCredential', assertionJson(credential));
            }
            return undefined;
        })
        .catch(() => {
            if (pendingAutofill === controller) {
                pendingAutofill = null;
            }
        });
    return true;
}
