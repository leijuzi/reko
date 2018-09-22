﻿#region License
/* 
 * Copyright (C) 1999-2018 John Källén.
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2, or (at your option)
 * any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; see the file COPYING.  If not, write to
 * the Free Software Foundation, 675 Mass Ave, Cambridge, MA 02139, USA.
 */
#endregion

using Reko.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Reko.Core.Expressions;
using Reko.Core.Types;
using Reko.Core.Code;

namespace Reko.Analysis
{
    /// <summary>
    /// Builds an application from a call instruction.
    /// </summary>
    public class CallApplicationBuilder : ApplicationBuilder, StorageVisitor<Expression>
    {
        private readonly SsaState ssaCaller;
        private readonly Statement stmCall;
        private readonly IProcessorArchitecture arch;
        private readonly int stackDepthOnEntry;
        private readonly Dictionary<StorageDomain, CallBinding> defs;
        private readonly Dictionary<StorageDomain, CallBinding> uses;
        private Dictionary<StorageDomain, CallBinding> map;

        public CallApplicationBuilder(SsaState ssaCaller, Statement stmCall, CallInstruction call, Expression callee) : base(call.CallSite, callee)
        {
            this.ssaCaller = ssaCaller;
            this.stmCall = stmCall;
            this.arch = ssaCaller.Procedure.Architecture;
            this.defs = call.Definitions.ToDictionary(u => u.Storage.Domain);
            var uses = new Dictionary<StorageDomain, CallBinding>();
            foreach (var u in call.Uses)
            {
                uses.Add(u.Storage.Domain, u);
            }
            this.uses = call.Uses.ToDictionary(u => u.Storage.Domain);
            this.stackDepthOnEntry = site.StackDepthOnEntry;
        }

        public override Expression Bind(Identifier id)
        {
            return With(uses, id.Storage);
        }

        public override OutArgument BindOutArg(Identifier id)
        {
            var exp = With(defs, id.Storage);
            return new OutArgument(arch.FramePointerType, exp);
        }

        public override Expression BindReturnValue(Identifier id)
        {
            return With(defs, id.Storage);
        }

        private Expression With(Dictionary<StorageDomain, CallBinding> map, Storage stg)
        {
            this.map = map;
            var exp = stg.Accept(this);
            this.map = null;
            return exp;
        }

        public Expression VisitFlagGroupStorage(FlagGroupStorage grf)
        {
            return map[grf.Domain].Expression;
        }

        public Expression VisitFpuStackStorage(FpuStackStorage fpu)
        {
            foreach (var de in this.map
              .Where(d => d.Value.Storage is FpuStackStorage))
            {
                if (((FpuStackStorage)de.Value.Storage).FpuStackOffset == fpu.FpuStackOffset)
                    return de.Value.Expression;
            }
            throw new NotImplementedException(string.Format("Offsets not matching? SP({0})", fpu.FpuStackOffset));
        }

        public Expression VisitMemoryStorage(MemoryStorage global)
        {
            throw new NotImplementedException();
        }

        public Expression VisitOutArgumentStorage(OutArgumentStorage arg)
        {
            if (defs.TryGetValue(arg.OriginalIdentifier.Storage.Domain, out var binding))
            {
                return binding.Expression;
            }
            else
            {
                // This out variable is dead, but we need to create a dummy identifier
                // for it to maintain the consistency of the SSA graph.
                var sid = ssaCaller.Identifiers.Add(arg.OriginalIdentifier, stmCall, null, true);
                return sid.Identifier;
            }
        }

        public Expression VisitRegisterStorage(RegisterStorage reg)
        {
            return map.TryGetValue(reg.Domain, out CallBinding cb)
                ? cb.Expression
                : null;
        }

        public Expression VisitSequenceStorage(SequenceStorage seq)
        {
            throw new NotImplementedException();
        }

        public Expression VisitStackArgumentStorage(StackArgumentStorage stack)
        {
            int localOff = stack.StackOffset - stackDepthOnEntry;
            foreach (var de in this.map
                .Where(d => d.Value.Storage is StackStorage))
            {
                if (((StackStorage)de.Value.Storage).StackOffset == localOff)
                    return de.Value.Expression;
            }
            return FallbackArgument($"stackArg{localOff}", stack.DataType);
        }

        public Expression VisitStackLocalStorage(StackLocalStorage local)
        {
            throw new NotImplementedException();
        }

        public Expression VisitTemporaryStorage(TemporaryStorage temp)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Argument was not found in the map due to bugs at previous stages.
        /// Create '<paramref name="name"/> = Constant.Invalid' statement.
        /// </summary>
        /// <param name="name">Argument name</param>
        /// <param name="dt">Argument type</param>
        /// <returns>
        /// Identifier with name '<paramref name="name"/>' as fallback argument
        /// </returns>
        private Identifier FallbackArgument(string name, DataType dt)
        {
            var id = ssaCaller.Procedure.Frame.CreateTemporary(name, dt);
            var sid = ssaCaller.Identifiers.Add(id, null, null, false);
            DefineUninitializedIdentifier(stmCall, sid);
            sid.Uses.Add(stmCall);
            return sid.Identifier;
        }

        private void DefineUninitializedIdentifier(
            Statement stm,
            SsaIdentifier sid)
        {
            var value = Constant.Invalid;
            var ass = new Assignment(sid.Identifier, value);
            var newStm = InsertStatementBefore(ass, stm);
            sid.DefExpression = value;
            sid.DefStatement = newStm;
            var comment =
@"Failed to bind call argument.
Please report this issue at https://github.com/uxmal/reko";
            InsertStatementBefore(new CodeComment(comment), newStm);
        }

        /// <summary>
        /// Inserts the instruction <paramref name="instr"/> into a
        /// Block before the statement <paramref name="stmBefore"/>.
        /// </summary>
        /// <param name="instr"></param>
        /// <param name="stmBefore"></param>
        /// <returns></returns>
        private Statement InsertStatementBefore(Instruction instr, Statement stmBefore)
        {
            var block = stmBefore.Block;
            var iPos = block.Statements.IndexOf(stmBefore);
            var linAddr = stmBefore.LinearAddress;
            return block.Statements.Insert(iPos, linAddr, instr);
        }
    }
}
