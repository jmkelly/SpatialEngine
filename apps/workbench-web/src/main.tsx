import { createRoot } from "react-dom/client";
import { WorkbenchProvider } from "./state.tsx";
import { App } from "./App.tsx";
import "./styles.css";

createRoot(document.getElementById("root")!).render(
  <WorkbenchProvider>
    <App />
  </WorkbenchProvider>,
);