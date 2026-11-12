namespace Centrifugal.Centrifuge {


    /// <summary>Listens to the main client. OnMainEvent is posted to the pool loop.</summary>
    internal interface IRpcPoolListener {
        /// <summary>Called on the pool loop when the main client changed its phase or identity.</summary>
        void OnMainEvent();
    }
}
