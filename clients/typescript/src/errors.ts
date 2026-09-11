/** A transport- or service-level failure of the spatial host (non-2xx HTTP). */
export class SpatialApiError extends Error {
  readonly status: number;
  readonly code: string;

  constructor(
    status: number,
    code: string,
    message: string,
  ) {
    super(message);
    this.name = "SpatialApiError";
    this.status = status;
    this.code = code;
  }
}
