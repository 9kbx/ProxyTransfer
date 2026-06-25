const rawApiKey = new URLSearchParams(window.location.search).get('key')?.trim() ?? ''

const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.replace(/\/$/, '') ?? ''

export const requestApiKey = rawApiKey

export function hasValidRequestApiKey(): boolean {
    return requestApiKey.length > 0
}

export async function validateRequestApiKey(): Promise<boolean> {
    if (!hasValidRequestApiKey()) {
        return false
    }

    try {
        const response = await fetch(`${apiBaseUrl}/api/auth/validate`, {
            headers: {
                'x-apikey': requestApiKey,
            },
        })

        return response.ok
    } catch {
        return false
    }
}