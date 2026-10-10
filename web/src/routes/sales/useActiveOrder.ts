import { useCallback, useRef, useState } from "react";
import type { SalesOrder } from "../../api/cluckwork";
import { editableLine, lineChanged, lineDraft, type EditorDraft } from "./orderMath";

// active draft being built
export function useActiveOrder() {
  const [active, setActiveState] = useState<SalesOrder | null>(null);
  const activeIdRef = useRef<string | null>(null);
  const activeRef = useRef<SalesOrder | null>(null);
  const [editor, setEditorState] = useState<EditorDraft | null>(null);
  const editorRef = useRef<EditorDraft | null>(null);
  // Retry identity includes the accepted edit baseline as well as the payload.
  // Keep ambiguous attempts across cancellation, but retire them when the editor
  // accepts different server values (Reload, reopening, or a clean refresh).
  const itemUpdateAttempts = useRef(new Map<string, {
    quantity: number; unitPriceMinorUnits: number; key: string;
    serverQuantity: number; serverPrice: number;
  }>());
  // Async publications read the latest typing/cancellation, not their starting render.
  const setEditor = useCallback((draft: EditorDraft | null) => {
    if (draft) {
      const scope = `update-item:${draft.itemId}`;
      const attempt = itemUpdateAttempts.current.get(scope);
      if (attempt && (attempt.serverQuantity !== draft.serverQuantity || attempt.serverPrice !== draft.serverPrice)) {
        itemUpdateAttempts.current.delete(scope);
      }
    }
    editorRef.current = draft;
    setEditorState(draft);
  }, []);
  const setActive = useCallback((order: SalesOrder | null) => {
    const draft = editorRef.current;
    const item = editableLine(order, draft);
    if (!order || !draft || !item) {
      setEditor(null);
    } else if (draft.quantity === draft.seedQuantity && draft.price === draft.seedPrice) {
      setEditor(lineDraft(order, item));
    }
    activeRef.current = order;
    activeIdRef.current = order?.id ?? null;
    setActiveState(order);
  }, [setEditor]);
  const editingLine = editableLine(active, editor);
  const editConflict = !!editor && !!editingLine && lineChanged(editingLine, editor);
  const reloadEditor = () => {
    const order = activeRef.current;
    const item = editableLine(order, editorRef.current);
    if (order && item) setEditor(lineDraft(order, item));
  };
  return {
    active, activeIdRef, activeRef, editor, editorRef, itemUpdateAttempts,
    setEditor, setActive, editingLine, editConflict, reloadEditor,
  };
}

export type ActiveOrderState = ReturnType<typeof useActiveOrder>;
