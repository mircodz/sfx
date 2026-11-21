import { describe, expect, test } from "bun:test";
import { scopedPath } from "../src/lib/view";

describe("scopedPath", () => {
    test("adds the view to lists it filters", () => {
        expect(scopedPath("GET", "/applications", "payments")).toBe("/applications?view=payments");
        expect(scopedPath("GET", "/replicas", "payments")).toBe("/replicas?view=payments");
        expect(scopedPath("GET", "/events?hours=1", "a b")).toBe("/events?hours=1&view=a%20b");
        expect(scopedPath("GET", "/overview", "payments")).toBe("/overview?view=payments");
        expect(scopedPath("GET", "/load?nodes=true", "payments")).toBe("/load?nodes=true");
    });

    test("filters a node's replicas and packages, not the node list", () => {
        expect(scopedPath("GET", "/nodes/_Node_0", "p")).toBe("/nodes/_Node_0?view=p");
        expect(scopedPath("GET", "/nodes/_Node_0/packages", "p")).toBe("/nodes/_Node_0/packages?view=p");
        expect(scopedPath("GET", "/nodes", "p")).toBe("/nodes");
        expect(scopedPath("GET", "/nodes/_Node_0/raw", "p")).toBe("/nodes/_Node_0/raw");
    });

    test("leaves direct links, changes and no view alone", () => {
        expect(scopedPath("GET", "/applications/orders", "p")).toBe("/applications/orders");
        expect(scopedPath("POST", "/applications", "p")).toBe("/applications");
        expect(scopedPath("GET", "/applications", null)).toBe("/applications");
        expect(scopedPath("GET", "/events?hours=24&entity=fabric%3A%2Forders", "p")).toBe("/events?hours=24&entity=fabric%3A%2Forders");
    });
});
