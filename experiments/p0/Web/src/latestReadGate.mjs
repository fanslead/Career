export function createLatestReadGate() {
  let selectedId = '';
  let generation = 0;

  return {
    select(id) {
      selectedId = id;
      generation += 1;
    },

    async read(id, request, onSuccess, onFailure) {
      const requestGeneration = ++generation;
      let value;
      try {
        value = await request();
      } catch (error) {
        if (requestGeneration === generation && selectedId === id) {
          onFailure(error);
        }
        return false;
      }

      if (requestGeneration !== generation || selectedId !== id) return false;
      onSuccess(value);
      return true;
    },
  };
}
