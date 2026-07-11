import XCTest
import GRPCCore
@testable import BevelHelper

/// U2: capability-scoped HMAC authentication via AuthInterceptor.
final class AuthTests: XCTestCase {
    // MARK: - constantTimeEquals

    func testConstantTimeEqualsMatchesIdenticalStrings() {
        XCTAssertTrue(AuthInterceptor.constantTimeEquals("abc123", "abc123"))
    }

    func testConstantTimeEqualsRejectsDifferentStrings() {
        XCTAssertFalse(AuthInterceptor.constantTimeEquals("abc", "abd"))
        XCTAssertFalse(AuthInterceptor.constantTimeEquals("abc", "abcd")) // length differs
        XCTAssertFalse(AuthInterceptor.constantTimeEquals("", "x"))
    }

    // MARK: - HMAC computation

    func testComputeHmacIsDeterministic() {
        let h1 = AuthInterceptor.computeHmac(key: "key", capability: "supervision")
        let h2 = AuthInterceptor.computeHmac(key: "key", capability: "supervision")
        XCTAssertEqual(h1, h2)
    }

    func testComputeHmacDifferentCapabilitiesProduceDifferentResults() {
        let hSuper = AuthInterceptor.computeHmac(key: "key", capability: "supervision")
        let hWindows = AuthInterceptor.computeHmac(key: "key", capability: "windows")
        XCTAssertNotEqual(hSuper, hWindows)
    }

    func testComputeHmacDifferentKeysProduceDifferentResults() {
        let h1 = AuthInterceptor.computeHmac(key: "key1", capability: "supervision")
        let h2 = AuthInterceptor.computeHmac(key: "key2", capability: "supervision")
        XCTAssertNotEqual(h1, h2)
    }

    // MARK: - capability from method

    func testCapabilityFromMethod_SupervisionService() {
        let cap = AuthInterceptor.capabilityFromMethod(
            "bevel.helper.v1.SupervisionService/Ping")
        XCTAssertEqual(cap, "supervision")
    }

    func testCapabilityFromMethod_WindowService() {
        let cap = AuthInterceptor.capabilityFromMethod(
            "bevel.helper.v1.WindowService/ListWindows")
        XCTAssertEqual(cap, "window")
    }

    func testCapabilityFromMethod_Unknown() {
        let cap = AuthInterceptor.capabilityFromMethod(
            "bevel.helper.v1.UnknownService/BadMethod")
        XCTAssertEqual(cap, "unknown")
    }

    // MARK: - token authentication

    func testAuthenticateAcceptsMatchingToken() throws {
        let hmac = AuthInterceptor.computeHmac(key: "s3cret", capability: "supervision")
        var metadata = Metadata()
        metadata.addString("supervision:\(hmac)", forKey: AuthInterceptor.tokenHeader)
        XCTAssertNoThrow(try AuthInterceptor.authenticate(
            metadata, expectedKey: "s3cret", expectedCapability: "supervision"))
    }

    func testAuthenticateRejectsWrongCapability() {
        let hmac = AuthInterceptor.computeHmac(key: "s3cret", capability: "supervision")
        var metadata = Metadata()
        metadata.addString("supervision:\(hmac)", forKey: AuthInterceptor.tokenHeader)
        XCTAssertThrowsError(try AuthInterceptor.authenticate(
            metadata, expectedKey: "s3cret", expectedCapability: "windows"))
    }

    func testAuthenticateRejectsWrongKey() {
        let hmac = AuthInterceptor.computeHmac(key: "wrong-key", capability: "supervision")
        var metadata = Metadata()
        metadata.addString("supervision:\(hmac)", forKey: AuthInterceptor.tokenHeader)
        XCTAssertThrowsError(try AuthInterceptor.authenticate(
            metadata, expectedKey: "s3cret", expectedCapability: "supervision"))
    }

    func testAuthenticateRejectsMissingToken() {
        let metadata = Metadata()
        XCTAssertThrowsError(try AuthInterceptor.authenticate(
            metadata, expectedKey: "s3cret", expectedCapability: "supervision"))
    }

    func testAuthenticateRejectsMalformedToken_NoColon() {
        var metadata = Metadata()
        metadata.addString("just-a-plain-string", forKey: AuthInterceptor.tokenHeader)
        XCTAssertThrowsError(try AuthInterceptor.authenticate(
            metadata, expectedKey: "s3cret", expectedCapability: "supervision"))
    }
}