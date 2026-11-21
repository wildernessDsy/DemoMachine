mergeInto(LibraryManager.library, {
  MachineBridge_Push: function (machine, rpm, spindle, px, py, pz) {
    try {
      if (typeof window.__pushMachine === "function") {
        window.__pushMachine(machine, rpm, spindle, px, py, pz);
      }
    } catch (e) {
      console.warn("MachineBridge_Push failed", e);
    }
  }
});
