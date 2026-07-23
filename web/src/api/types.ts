// Mirrors the backend contract (Cluckwork.Api /api/v1/auth).
// #145 — login/refresh return only the access token; the refresh token is
// delivered as an HttpOnly cookie (AccessTokenResponse in AuthEndpoints.cs).
export interface AccessTokenResponse {
  accessToken: string;
  accessTokenExpiry: string; // ISO-8601
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
