// Mirrors the backend contract (Cluckwork.Api /api/v1/auth).
// TokenPair: src/Cluckwork.Application/Common/IIdentityProvider.cs
export interface TokenPair {
  accessToken: string;
  refreshToken: string;
  expiresAt: string; // ISO-8601
}

export interface LoginRequest {
  email: string;
  password: string;
}

// RFC 7807 problem response the API returns for auth failures.
export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
}
