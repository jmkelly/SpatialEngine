/**
 * The subset of the Web Crypto API {@link newId} needs. `randomUUID` is
 * optional because browsers only expose it in secure contexts.
 */
export interface IdCrypto {
  randomUUID?: (() => string) | undefined;
  getRandomValues<T extends ArrayBufferView>(array: T): T;
}

/**
 * Generates a random identifier.
 *
 * `crypto.randomUUID` only exists in secure contexts (HTTPS or localhost), so
 * the workbench served over plain HTTP on a LAN would otherwise throw.
 * `crypto.getRandomValues` is available in every context, so fall back to it
 * and assemble an RFC 4122 version 4 UUID by hand.
 */
export function newId(source: IdCrypto = crypto): string {
  if (typeof source.randomUUID === "function") {
    return source.randomUUID();
  }

  const bytes = new Uint8Array(16);
  source.getRandomValues(bytes);
  bytes[6] = (bytes[6]! & 0x0f) | 0x40;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
