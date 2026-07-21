using System.Text.Json;

namespace SessionDock.Services;

internal static class RobloxWebScripts
{
    public static string GetAuthenticatedUser(string requestId)
    {
        var id = JsonSerializer.Serialize(requestId);
        return $$"""
            (() => {
                fetch('https://users.roblox.com/v1/users/authenticated', {
                    credentials: 'include'
                })
                .then(async response => {
                    const user = response.ok ? await response.json() : null;
                    window.chrome.webview.postMessage({ requestId: {{id}}, user });
                })
                .catch(() => window.chrome.webview.postMessage({
                    requestId: {{id}},
                    user: null
                }));
            })();
            """;
    }

    public static string ResolvePrivateServer(string requestId, string shareCode)
    {
        var id = JsonSerializer.Serialize(requestId);
        var code = JsonSerializer.Serialize(shareCode);
        return $$"""
            (async () => {
                let placeId = 0;
                let linkCode = null;
                try {
                    const resolve = csrfToken => fetch(
                        'https://apis.roblox.com/sharelinks/v1/resolve-link',
                        {
                            method: 'POST',
                            credentials: 'include',
                            headers: {
                                'Content-Type': 'application/json',
                                ...(csrfToken ? { 'x-csrf-token': csrfToken } : {})
                            },
                            body: JSON.stringify({
                                linkId: {{code}},
                                linkType: 'Server'
                            })
                        });

                    let response = await resolve(null);
                    if (response.status === 403) {
                        const csrfToken = response.headers.get('x-csrf-token');
                        if (csrfToken)
                            response = await resolve(csrfToken);
                    }

                    if (response.ok) {
                        const resolution = await response.json();
                        const invite = resolution?.privateServerInviteData;
                        if (invite?.status === 'Valid' && invite?.universeId && invite?.linkCode) {
                            const gameResponse = await fetch(
                                `https://games.roblox.com/v1/games?universeIds=${invite.universeId}`,
                                { credentials: 'include' });
                            if (gameResponse.ok) {
                                const games = await gameResponse.json();
                                placeId = Number(games?.data?.[0]?.rootPlaceId ?? 0);
                                linkCode = invite.linkCode;
                            }
                        }
                    }
                } catch {}

                window.chrome.webview.postMessage({
                    requestId: {{id}},
                    placeId,
                    linkCode
                });
            })();
            """;
    }

    public static string GetAuthenticationTicket(string requestId)
    {
        var id = JsonSerializer.Serialize(requestId);
        return $$"""
            (async () => {
                let ticket = null;
                try {
                    let assertion = {};
                    try {
                        const assertionResponse = await fetch(
                            'https://auth.roblox.com/v1/client-assertion/',
                            { credentials: 'include' });
                        if (assertionResponse.ok)
                            assertion = await assertionResponse.json();
                    } catch {}

                    const requestTicket = csrfToken => fetch(
                        'https://auth.roblox.com/v1/authentication-ticket/',
                        {
                            method: 'POST',
                            credentials: 'include',
                            headers: {
                                'Content-Type': 'application/json',
                                ...(csrfToken ? { 'x-csrf-token': csrfToken } : {})
                            },
                            body: JSON.stringify(assertion ?? {})
                        });

                    let response = await requestTicket(null);
                    if (response.status === 403) {
                        const csrfToken = response.headers.get('x-csrf-token');
                        if (csrfToken)
                            response = await requestTicket(csrfToken);
                    }

                    if (response.ok)
                        ticket = response.headers.get('rbx-authentication-ticket');
                } catch {}

                window.chrome.webview.postMessage({
                    requestId: {{id}},
                    ticket
                });
            })();
            """;
    }

    public static string GetUserLocale(string requestId)
    {
        var id = JsonSerializer.Serialize(requestId);
        return $$"""
            (async () => {
                let locale = null;
                try {
                    const response = await fetch(
                        'https://locale.roblox.com/v1/locales/user-locale',
                        { credentials: 'include' });
                    if (response.ok) {
                        const value = await response.json();
                        locale = value?.supportedLocale?.locale ?? null;
                    }
                } catch {}

                window.chrome.webview.postMessage({
                    requestId: {{id}},
                    locale
                });
            })();
            """;
    }

    public static string GetExperienceName(string requestId, long placeId)
    {
        var id = JsonSerializer.Serialize(requestId);
        return $$"""
            (async () => {
                let name = null;
                try {
                    const response = await fetch(
                        'https://games.roblox.com/v1/games/multiget-place-details?placeIds={{placeId}}',
                        { credentials: 'include' });
                    if (response.ok) {
                        const places = await response.json();
                        name = places?.[0]?.name ?? null;
                    }
                } catch {}

                window.chrome.webview.postMessage({
                    requestId: {{id}},
                    name
                });
            })();
            """;
    }
}
