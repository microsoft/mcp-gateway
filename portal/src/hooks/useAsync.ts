// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "../api/client";

export interface AsyncState<T> {
  data: T | undefined;
  error: Error | undefined;
  loading: boolean;
  reload: () => void;
}

/**
 * Tiny `useAsync` so we don't pull a full data-fetching library for the few
 * places that need refresh-on-demand semantics. Cancels in-flight work when
 * the component unmounts or the key changes.
 */
export function useAsync<T>(
  factory: (signal: AbortSignal) => Promise<T>,
  deps: ReadonlyArray<unknown>,
): AsyncState<T> {
  const [data, setData] = useState<T | undefined>(undefined);
  const [error, setError] = useState<Error | undefined>(undefined);
  const [loading, setLoading] = useState<boolean>(true);
  const [tick, setTick] = useState(0);
  const factoryRef = useRef(factory);
  factoryRef.current = factory;

  const reload = useCallback(() => setTick((n) => n + 1), []);

  useEffect(() => {
    const ac = new AbortController();
    setLoading(true);
    setError(undefined);
    factoryRef.current(ac.signal).then(
      (value) => {
        if (ac.signal.aborted) return;
        setData(value);
        setLoading(false);
      },
      (err) => {
        if (ac.signal.aborted) return;
        setError(err instanceof Error ? err : new Error(String(err)));
        setLoading(false);
      },
    );
    return () => ac.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick]);

  return { data, error, loading, reload };
}

export function formatApiError(err: unknown): string {
  if (err instanceof ApiError) {
    const detail = err.body && err.body.length < 400 ? ` — ${err.body}` : "";
    return `${err.message}${detail}`;
  }
  if (err instanceof Error) return err.message;
  return String(err);
}
