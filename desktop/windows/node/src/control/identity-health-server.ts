import { createServer, type Server, type ServerResponse } from 'node:http';
import type { Socket } from 'node:net';

import {
  MAX_ACTIVE_WORK_REMAINING,
  MAX_GENERATION,
  MAX_TEXT_FIELD_CHARS,
} from '../protocol/control-protocol.js';

const LOOPBACK_HOST = '127.0.0.1';
const HEALTH_PATH = '/health';
const MAXIMUM_CLIENTS = 8;
const MAXIMUM_REQUEST_BYTES = 8 * 1024;
const REQUEST_TIMEOUT_MS = 1_000;
const NOT_FOUND_BODY = Buffer.from('not found', 'utf8');

export interface IdentityHealthServerOptions {
  port: number;
  protocolVersion: 2;
  buildIdentity: string;
  generation: number;
  generationNonce: string;
  allowEphemeralPort?: boolean;
}

type HealthStatus = 'not-ready' | 'ready';

type HealthPayload = {
  protocolVersion: 2;
  buildIdentity: string;
  generation: number;
  generationNonce: string;
  status: HealthStatus;
  activeWorkRemaining: number;
};

export type IdentityHealthServerErrorCode =
  | 'invalid-options'
  | 'invalid-active-work'
  | 'not-listening'
  | 'server-closed'
  | 'listen-failed'
  | 'close-failed';

export class IdentityHealthServerError extends Error {
  constructor(public readonly code: IdentityHealthServerErrorCode) {
    super(`Identity health server error: ${code}.`);
    this.name = 'IdentityHealthServerError';
  }
}

export class IdentityHealthServer implements AsyncDisposable {
  readonly #options: Readonly<IdentityHealthServerOptions>;
  readonly #server: Server;
  readonly #sockets = new Set<Socket>();
  #payload: HealthPayload;
  #listenPromise: Promise<void> | undefined;
  #closePromise: Promise<void> | undefined;
  #listening = false;
  #closing = false;

  constructor(options: IdentityHealthServerOptions) {
    validateOptions(options);
    this.#options = Object.freeze({ ...options });
    this.#payload = {
      protocolVersion: options.protocolVersion,
      buildIdentity: options.buildIdentity,
      generation: options.generation,
      generationNonce: options.generationNonce,
      status: 'not-ready',
      activeWorkRemaining: 0,
    };
    this.#server = createServer(
      {
        maxHeaderSize: MAXIMUM_REQUEST_BYTES,
        headersTimeout: REQUEST_TIMEOUT_MS,
        requestTimeout: REQUEST_TIMEOUT_MS,
        connectionsCheckingInterval: 100,
        keepAlive: false,
        requireHostHeader: true,
      },
      (request, response) => {
        const accepted =
          request.method === 'GET' &&
          request.url === HEALTH_PATH &&
          request.headers['x-apphost-protocol'] ===
            String(this.#options.protocolVersion) &&
          request.headers['x-apphost-build'] === this.#options.buildIdentity &&
          request.headers['x-apphost-generation'] ===
            String(this.#options.generation) &&
          request.headers['x-apphost-nonce'] === this.#options.generationNonce;

        if (!accepted) {
          writeResponse(response, 404, 'Not Found', NOT_FOUND_BODY);
          return;
        }

        writeResponse(
          response,
          200,
          'OK',
          Buffer.from(JSON.stringify(this.#payload), 'utf8'),
        );
      },
    );
    this.#server.maxRequestsPerSocket = 1;
    this.#server.on('connection', (socket: Socket) => {
      if (this.#closing || this.#sockets.size >= MAXIMUM_CLIENTS) {
        socket.destroy();
        return;
      }

      this.#sockets.add(socket);
      socket.setTimeout(REQUEST_TIMEOUT_MS, () => socket.destroy());
      socket.once('close', () => this.#sockets.delete(socket));
    });
    this.#server.on('clientError', (_error, socket) => {
      if (!socket.writable || socket.destroyed) {
        socket.destroy();
        return;
      }
      socket.end(fixedNotFoundResponse());
    });
  }

  get endpoint(): string {
    const address = this.#server.address();
    if (address === null || typeof address === 'string') {
      throw new IdentityHealthServerError('not-listening');
    }
    return `http://${LOOPBACK_HOST}:${address.port}${HEALTH_PATH}`;
  }

  listen(): Promise<void> {
    if (this.#closing) {
      return Promise.reject(new IdentityHealthServerError('server-closed'));
    }
    if (this.#listenPromise !== undefined) {
      return this.#listenPromise;
    }

    this.#listenPromise = new Promise<void>((resolve, reject) => {
      const onError = (): void => {
        this.#server.off('listening', onListening);
        reject(new IdentityHealthServerError('listen-failed'));
      };
      const onListening = (): void => {
        this.#server.off('error', onError);
        this.#listening = true;
        resolve();
      };
      this.#server.once('error', onError);
      this.#server.once('listening', onListening);
      try {
        this.#server.listen({
          port: this.#options.port,
          host: LOOPBACK_HOST,
          backlog: MAXIMUM_CLIENTS,
          exclusive: true,
        });
      } catch {
        this.#server.off('error', onError);
        this.#server.off('listening', onListening);
        reject(new IdentityHealthServerError('listen-failed'));
      }
    });
    return this.#listenPromise;
  }

  markReady(activeWorkRemaining = 0): void {
    if (this.#closing) {
      throw new IdentityHealthServerError('server-closed');
    }
    if (!this.#listening) {
      throw new IdentityHealthServerError('not-listening');
    }
    validateActiveWorkRemaining(activeWorkRemaining);
    this.#payload = {
      ...this.#payload,
      status: 'ready',
      activeWorkRemaining,
    };
  }

  close(): Promise<void> {
    if (this.#closePromise !== undefined) {
      return this.#closePromise;
    }

    this.#closing = true;
    this.#closePromise = this.#closeCore();
    return this.#closePromise;
  }

  async #closeCore(): Promise<void> {
    const listenSucceeded = await this.#listenPromise?.then(
      () => true,
      () => false,
    );
    if (listenSucceeded !== true || !this.#server.listening) {
      this.#listening = false;
      for (const socket of this.#sockets) socket.destroy();
      return;
    }

    const closed = new Promise<void>((resolve, reject) => {
      this.#server.close((error) => {
        if (error !== undefined)
          reject(new IdentityHealthServerError('close-failed'));
        else resolve();
      });
    });
    this.#server.closeAllConnections();
    for (const socket of this.#sockets) socket.destroy();
    try {
      await closed;
    } finally {
      this.#listening = false;
    }
  }

  [Symbol.asyncDispose](): Promise<void> {
    return this.close();
  }
}

function validateOptions(options: IdentityHealthServerOptions): void {
  if (typeof options !== 'object' || options === null) {
    throw new IdentityHealthServerError('invalid-options');
  }
  if (options.protocolVersion !== 2) {
    throw new IdentityHealthServerError('invalid-options');
  }
  if (
    !Number.isSafeInteger(options.generation) ||
    options.generation < 0 ||
    options.generation > MAX_GENERATION
  ) {
    throw new IdentityHealthServerError('invalid-options');
  }
  validateIdentityText(options.buildIdentity);
  validateIdentityText(options.generationNonce);
  if (
    !Number.isInteger(options.port) ||
    options.port < 0 ||
    options.port > 65_535 ||
    (options.port === 0
      ? options.allowEphemeralPort !== true
      : options.port < 1_024)
  ) {
    throw new IdentityHealthServerError('invalid-options');
  }
}

function validateIdentityText(value: string): void {
  if (
    typeof value !== 'string' ||
    value.trim().length === 0 ||
    value.length > MAX_TEXT_FIELD_CHARS
  ) {
    throw new IdentityHealthServerError('invalid-options');
  }
}

function validateActiveWorkRemaining(value: number): void {
  if (
    !Number.isInteger(value) ||
    value < 0 ||
    value > MAX_ACTIVE_WORK_REMAINING
  ) {
    throw new IdentityHealthServerError('invalid-active-work');
  }
}

function writeResponse(
  response: ServerResponse,
  statusCode: number,
  statusMessage: string,
  body: Buffer,
): void {
  response.shouldKeepAlive = false;
  response.writeHead(statusCode, statusMessage, {
    'Content-Type': 'application/json',
    'Content-Length': body.byteLength,
    Connection: 'close',
  });
  response.end(body);
}

function fixedNotFoundResponse(): Buffer {
  return Buffer.from(
    `HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\nContent-Length: ${NOT_FOUND_BODY.byteLength}\r\nConnection: close\r\n\r\nnot found`,
    'ascii',
  );
}
