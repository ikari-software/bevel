import XCTest
import GRPCCore
@testable import BevelHelper

/// bevel-164: the helper must authenticate RPCs with the per-session nonce.
final class AuthTests: XCTestCase {
    func testConstantTimeEqualsMatchesIdenticalStrings() {
        XCTAssertTrue(SupervisionServiceImpl.constantTimeEquals("abc123", "abc123"))
    }

    func testConstantTimeEqualsRejectsDifferentStrings() {
        XCTAssertFalse(SupervisionServiceImpl.constantTimeEquals("abc", "abd"))
        XCTAssertFalse(SupervisionServiceImpl.constantTimeEquals("abc", "abcd")) // length differs
        XCTAssertFalse(SupervisionServiceImpl.constantTimeEquals("", "x"))
    }

    func testAuthenticateAcceptsMatchingToken() throws {
        var metadata = Metadata()
        metadata.addString("s3cret-nonce", forKey: SupervisionServiceImpl.tokenHeader)
        XCTAssertNoThrow(try SupervisionServiceImpl.authenticate(metadata, expectedToken: "s3cret-nonce"))
    }

    func testAuthenticateRejectsWrongToken() {
        var metadata = Metadata()
        metadata.addString("wrong", forKey: SupervisionServiceImpl.tokenHeader)
        XCTAssertThrowsError(try SupervisionServiceImpl.authenticate(metadata, expectedToken: "s3cret-nonce"))
    }

    func testAuthenticateRejectsMissingToken() {
        let metadata = Metadata() // no token header
        XCTAssertThrowsError(try SupervisionServiceImpl.authenticate(metadata, expectedToken: "s3cret-nonce"))
    }
}
