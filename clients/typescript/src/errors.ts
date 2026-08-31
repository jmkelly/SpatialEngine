import type { CapabilityErrorDto } from "./generated-types.ts";

/** A transport- or resource-level failure of the spatial host (non-2xx HTTP). */
export class SpatialApiError extends Error {
  readonly status: number;

  constructor(
    status: number,
    message: string,
  ) {
    super(message);
    this.name = "SpatialApiError";
    this.status = status;
  }
}

/** A bounded stream ended (or was interrupted) with a structured capability failure. */
export class CapabilityStreamError extends Error {
  readonly error: CapabilityErrorDto;

  constructor(error: CapabilityErrorDto) {
    super(`the stream completed with ${error.code}: ${error.message}`);
    this.name = "CapabilityStreamError";
    this.error = error;
  }
}