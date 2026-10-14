export type LatestReadGate = {
  select(id: string): void;
  read<T>(
    id: string,
    request: () => Promise<T>,
    onSuccess: (value: T) => void,
    onFailure: (error: unknown) => void,
  ): Promise<boolean>;
};

export function createLatestReadGate(): LatestReadGate;
