using System;

namespace Centrifugal.Centrifuge {


    /// <summary>The token source as the rpc pool sees it. The pool engine and the pool connections use it through this interface.</summary>
    internal interface IRpcPoolTokenSource {
        /// <summary>Main client phase. Readable from any thread.</summary>
        RpcPoolMainState MainState { get; }

        /// <summary>Identity generation. Readable from any thread.</summary>
        long Generation { get; }

        /// <summary>The identity stamp. Read as one value on any thread.</summary>
        RpcPoolStamp Stamp { get; }

        /// <summary>The confirmed token may be lent to a pool connection.</summary>
        bool CanLend { get; }

        /// <summary>The source is spent: a refused token could not be renewed.</summary>
        bool IsSpent { get; }

        /// <summary>
        /// Asks for a token for requester. done runs on the main loop, after a loop boundary, with (error, token, generation).
        /// refusedToken is the token the connection was refused with, or null at the first request.
        /// </summary>
        void RequestToken(object requester, string? refusedToken, Action<Exception?, string?, long> done);

        /// <summary>Drops a waiting request. Its done is never called.</summary>
        void Forget(object requester);

        /// <summary>Marks a token as unusable so that it is not lent again.</summary>
        void MarkUnusable(string token);

        /// <summary>Adds a listener for main-client phase and identity changes. Pools listen only while a call waits.</summary>
        void Listen(IRpcPoolListener listener);

        /// <summary>Removes a listener.</summary>
        void Unlisten(IRpcPoolListener listener);
    }
}
