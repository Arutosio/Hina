using System;
using System.IO;

namespace Hina.Core.Net
{
    // A fetched chunk failed integrity: the body didn't Brotli-decode, or the decompressed
    // bytes don't hash to the content-addressed name. Both mean the store/CDN served bytes
    // that are not the named chunk — corruption at rest on one edge node is the common cause,
    // so RetryPolicy treats this as transient and re-fetches (bounded by maxRetries).
    // InvalidDataException is sealed, so this derives from IOException; no production code
    // catches InvalidDataException specifically, so the type change is safe.
    public sealed class ChunkIntegrityException : IOException
    {
        public ChunkIntegrityException(string message) : base(message) { }
        public ChunkIntegrityException(string message, Exception inner) : base(message, inner) { }
    }
}
